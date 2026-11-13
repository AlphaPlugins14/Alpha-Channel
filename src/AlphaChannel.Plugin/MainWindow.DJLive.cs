using AlphaChannel.Contracts;
using AlphaChannel.Plugin.Video;
using AlphaChannel.Plugin.BroadcastAllowance;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private string djStreamUrl = string.Empty;

    private bool djCredentialsLoading;
    private bool djPasswordVisible;
    private bool patreonAccessConfirmed;
    private string? djPatreonAccessMessage;

    private bool djConnectionCheckInProgress;
    private bool djConnectionSearchActive;
    private bool djConnectionMonitoringEnabled;
    private DateTime djConnectionCheckFlashUntilUtc = DateTime.MinValue;

    private bool djIsOnAir;
    private DateTime? djOnAirSinceUtc;

    internal bool IsDjActivityActive =>
        stream.Mode != StreamMode.Viewing &&
        (studioSessionActive || IsPlayingOwnIcecastDjStream);

    internal bool IsPlayingOwnIcecastDjStream =>
        djIsOnAir &&
        stream.Mode != StreamMode.Viewing &&
        radioCredentials is { } radio &&
        !string.IsNullOrWhiteSpace(radio.ListenUrl) &&
        queue.Current is { Source: "Music / DJ" } current &&
        current.Url.StartsWith(radio.ListenUrl, StringComparison.OrdinalIgnoreCase);

    private DateTime djConnectionSearchEndsUtc =
        DateTime.MinValue;

    private DateTime djNextConnectionCheckUtc =
        DateTime.MaxValue;



    private bool djConnectionGuideOpen;
    private int djConnectionGuideTab;

    private bool djAddStationPopupOpen;
    private int? djEditingStationIndex;
    private string? djStationEditorError;


    //
    // Radio UI
    //

    private string djStationNameInput = string.Empty;
    private string djStationUrlInput = string.Empty;

    private List<SavedRadioStationRecord> djSavedStations =>
     Plugin.Cfg.SavedRadioStations;

    //
    // Temporary legacy-guide state. These can be removed when the obsolete
    // guide methods themselves are deleted.
    //

    private bool djSimpleGuideOpen;
    private bool djAdvancedGuideOpen;

    private int djSimpleGuideStep;
    private int djAdvancedGuideStep;

    private enum PendingWatchPartyMediaKind
    {
        None,
        DjBroadcast,
        StudioBroadcast,
        LiveStreamBroadcast,
        LocalVideoStartAndBroadcast,
        LocalVideoCurrentPlaybackBroadcast,
        GameRoom,
        LocalVideoRoom,
        Whiteboard,
        Quiz,
        Pictionary,
        Roulette,
        Bingo,
        SkyRail,
        SlotMachine,
        Poker,
        ChocoRace,
        WebLink,
        RadioStation,
        LuckyWheel,
        Pixels,
        BlastParty,
        TwitchRoom,
    }

    private PendingWatchPartyMediaKind pendingWatchPartyMediaKind;
    private Video.VideoQueueEntry? pendingWatchPartyVideoEntry;
    private string? pendingWatchPartyRadioUrl;
    private string? pendingWatchPartyRadioTitle;
    private bool watchPartyCreationPopupOpen;
    private bool djBroadcastingToWatchParty;
    private bool djAutoMuteNoticeVisible;
    private string? djAutoMutedStreamUrl;


    private void DrawDjLiveHeading()
    {
        var origin =
            ImGui.GetCursorScreenPos();

        using var uiFontScale4429=UiFonts.PushRelative(1.35f);

        ImGui.TextColored(
            Vector4.One,
            "DJ Live");

        uiFontScale4429.Dispose();

        ImGui.Dummy(
            UiVec(0f, 2f));

        using var uiFontScale4656=UiFonts.PushRelative(0.84f);

        ImGui.TextColored(
            MutedText,
            "Broadcast live audio using Icecast-compatible DJ software.");

        uiFontScale4656.Dispose();

        DrawDjOnAirBadge(
            origin);
    }

    private void DrawDjOnAirBadge(
        Vector2 headingOrigin)
    {
        var label =
            djConnectionSearchActive
                ? "CHECKING"
                : djIsOnAir
                    ? djOnAirSinceUtc is { } started
                        ? $"ON AIR · {FormatDjOnAirDuration(DateTime.UtcNow - started)}"
                        : "ON AIR"
                    : "OFF AIR";

        var color =
            djConnectionSearchActive
                ? Gold
                : djIsOnAir
                    ? new Vector4(
                        0.94f,
                        0.16f,
                        0.20f,
                        1f)
                    : MutedText;

        var textSize =
            ImGui.CalcTextSize(
                label);

        var badgeSize =
            new Vector2(
                textSize.X + Ui(40f),
                Ui(38f));

        var badgeOrigin =
            new Vector2(
                ImGui.GetWindowPos().X +
                ImGui.GetWindowWidth() -
                badgeSize.X -
                Ui(18f),
                headingOrigin.Y);

        var drawList =
            ImGui.GetWindowDrawList();

        drawList.AddRectFilled(
            badgeOrigin,
            badgeOrigin + badgeSize,
            ImGui.GetColorU32(
                new Vector4(
                    color.X,
                    color.Y,
                    color.Z,
                    djIsOnAir
                        ? 0.25f
                        : 0.11f)),
            10f);

        drawList.AddRect(
            badgeOrigin,
            badgeOrigin + badgeSize,
            ImGui.GetColorU32(
                new Vector4(
                    color.X,
                    color.Y,
                    color.Z,
                    0.65f)),
            10f,
            ImDrawFlags.RoundCornersAll,
            1f);

        drawList.AddCircleFilled(
            new Vector2(
                badgeOrigin.X + Ui(17f),
                badgeOrigin.Y +
                badgeSize.Y * 0.5f),
            6f,
            ImGui.GetColorU32(
                color),
            20);

        UiFonts.DrawText(drawList, ImGui.GetFontSize(), new Vector2(
                badgeOrigin.X + Ui(29f),
                badgeOrigin.Y +
                (badgeSize.Y - textSize.Y) * 0.5f), ImGui.GetColorU32(
                djIsOnAir
                    ? Vector4.One
                    : color), label);
    }

    private void DrawDjBroadcastCard()
    {
        var cardHeight =
            radioCredentials is null
                ? radioError is { Length: > 0 }
                    ? 175f
                    : 150f
                : djBroadcastingToWatchParty &&
                  stream.Mode == StreamMode.Hosting &&
                  video.IsAudioOnly &&
                  queue.Current is { Source: "Music / DJ" }
                    ? 638f
                    : 588f;

        using var cardStyle =
            ImRaii.PushStyle(
                    ImGuiStyleVar.ChildRounding,
                    12f)
                .Push(
                    ImGuiStyleVar.ChildBorderSize,
                    1f)
                .Push(
                    ImGuiStyleVar.WindowPadding,
                    UiVec(20f, 18f));

        using var cardColors =
            ImRaii.PushColor(
                    ImGuiCol.ChildBg,
                    CardBg)
                .Push(
                    ImGuiCol.Border,
                    new Vector4(
                        Accent.X,
                        Accent.Y,
                        Accent.Z,
                        0.38f));

        if (UiFonts.Window(ImGui.GetFontSize(), ImGui.BeginChild(
                "##djBroadcastCard",
                new Vector2(
                    -1f,
                    cardHeight),
                true,
                ImGuiWindowFlags.NoScrollbar |
                ImGuiWindowFlags.NoScrollWithMouse)))
        {
            DrawDjBroadcastCardHeading();

            ImGui.Dummy(
                UiVec(0f, 14f));

            if (radioCredentials is null)
            {
                DrawDjCredentialsEmptyState();
            }
            else
            {
                DrawDjCredentials(
                    radioCredentials);
            }
        }

        ImGui.EndChild();
    }

    private void DrawDjBroadcastCardHeading()
    {
        var icon =
            FontAwesomeIcon.BroadcastTower
                .ToIconString();

        using (UiFonts.PushIcon())
        {
            using var uiFontScale9662=UiFonts.PushRelative(1.35f);

            ImGui.TextColored(
                Accent,
                icon);

            uiFontScale9662.Dispose();
        }

        ImGui.SameLine(
            0f,
            12f);

        ImGui.BeginGroup();

        using var uiFontScale9955=UiFonts.PushRelative(1.08f);

        ImGui.TextColored(
            Vector4.One,
            "Broadcast connection");

        uiFontScale9955.Dispose();

        ImGui.TextColored(
            MutedText,
            "Use these settings in any Icecast compatible DJ software to broadcast live to your Alpha Channel Watch Party");

        ImGui.EndGroup();
    }

    private void DrawDjCredentialsEmptyState()
    {
        const string prompt =
            "Get the broadcast connection details before connecting your DJ software.";

        var rowOrigin =
            ImGui.GetCursorScreenPos();

        var gap =
            Ui(20f);

        var columnWidth =
            (ImGui.GetContentRegionAvail().X - gap) * 0.5f;

        var buttonSize =
            new Vector2(
                columnWidth,
                Ui(40f));

        var textSize =
            ImGui.CalcTextSize(
                prompt,
                false,
                columnWidth);

        var rowHeight =
            MathF.Max(
                buttonSize.Y,
                textSize.Y);

        ImGui.SetCursorScreenPos(
            rowOrigin +
            new Vector2(
                0f,
                (rowHeight - textSize.Y) * 0.5f));

        ImGui.PushTextWrapPos(
            ImGui.GetCursorPos().X +
            columnWidth);

        ImGui.TextColored(
            MutedText,
            prompt);

        ImGui.PopTextWrapPos();

        ImGui.SetCursorScreenPos(
            rowOrigin +
            new Vector2(
                columnWidth + gap,
                (rowHeight - buttonSize.Y) * 0.5f));

        using (UiDisabled.Scope(
                   djCredentialsLoading))
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
            var buttonOrigin =
                ImGui.GetCursorScreenPos();

            if (ImGui.Button(
                    "##getDjCredentials",
                    buttonSize))
            {
                RequestDjConnectionDetails();
            }

            DrawPlayerActionButtonContent(
                buttonOrigin,
                buttonSize,
                FontAwesomeIcon.Key,
                djCredentialsLoading
                    ? "Getting details..."
                    : "Get connection details",
                Vector4.One);
        }

        ImGui.SetCursorScreenPos(
            rowOrigin +
            new Vector2(
                0f,
                rowHeight));

        if (radioError is { Length: > 0 } error)
        {
            ImGui.Dummy(
                UiVec(0f, 9f));

            ImGui.TextColored(
                Danger,
                error);
        }
    }

    private bool HasConfiguredPatreonAccess() => ConfiguredPatreonTier() > 0;

    private int ConfiguredPatreonTier() => CurrentVerifiedPatreonTier();

    private bool HasConfiguredPatreonTier(int requiredTier) =>
        ConfiguredPatreonTier() >= requiredTier;

    private void RequestDjConnectionDetails()
    {
        IssueAlphaChannelRadio();
    }

    private void RefreshDjPatreonAccess()
    {
        if (!HasConfiguredPatreonAccess())
        {
            patreonAccessConfirmed = false;
            djPatreonAccessMessage =
                "No active Patreon membership was found.";
            return;
        }

        patreonAccessConfirmed = true;
        djPatreonAccessMessage = null;
        radioError = null;


        IssueAlphaChannelRadio();
    }

    internal void NotifyLocalPatreonMembershipChanged()
    {
        patreonAccessConfirmed = false;
        djPatreonAccessMessage = null;

        localVideoPatreonAccessMessage = null;
        livePatreonAccessMessage = null;
        ResetObsRequests();

        if (!HasConfiguredPatreonAccess() &&
            Plugin.Cfg.LocalVideoBroadcastQuality != LocalVideoBroadcastQuality.DataSaver)
        {
            Plugin.Cfg.LocalVideoBroadcastQuality = LocalVideoBroadcastQuality.DataSaver;
            Plugin.Cfg.Save();

            if (localVideoBroadcastArmed)
            {
                screenController.Engine.StopLocalVideoBroadcast();
                localVideoBroadcastEncoderExpectedRunning = false;
                localVideoBroadcastStartFailed = false;
            }
        }

        if (!HasConfiguredPatreonAccess() &&
            Plugin.Cfg.GameBroadcastQuality == GameBroadcastQuality.StandardNative)
        {
            Plugin.Cfg.GameBroadcastQuality = GameBroadcastQuality.DataSaver;
            Plugin.Cfg.Save();

            if (gameBroadcastArmed)
            {
                StopGameBroadcastEncoder();
                gameBroadcastStartFailed = false;
                gameBroadcastViewerReadyUtc = DateTime.MaxValue;
            }
        }

        var permittedBrowserQuality = Plugin.Cfg.BrowserBroadcastQuality.ForPatreonTier(ConfiguredPatreonTier());
        if (Plugin.Cfg.BrowserBroadcastQuality != permittedBrowserQuality)
        {
            Plugin.Cfg.BrowserBroadcastQuality = permittedBrowserQuality;
            Plugin.Cfg.Save();

            if (browserBroadcastArmed)
            {
                screenController.Engine.StopBrowserBroadcast();
                browserBroadcastStartFailed = false;
            }
        }
    }

    private void DrawDjPatreonLockedActions(
        float availableWidth)
    {
        var gap = NativeUi(9f);
        var smallWidth = Ui(170f);
        var actionHeight = Ui(38f);

        DrawDjActionButton(
            "##copyLockedDjSettings",
            FontAwesomeIcon.Clipboard,
            "Copy all settings",
            new Vector2(
                smallWidth,
                actionHeight),
            true,
            () => { },
            false,
            MutedText);

        if (ImGui.IsItemHovered(
                ImGuiHoveredFlags.AllowWhenDisabled))
        {
            UiFonts.SetTooltip(
                "A Patreon membership is required to access DJ connection details.");
        }

        ImGui.SameLine(
            0f,
            gap);

        DrawDjActionButton(
            "##checkLockedDjConnection",
            FontAwesomeIcon.BroadcastTower,
            "Check connection",
            new Vector2(
                smallWidth,
                actionHeight),
            false,
            () =>
            {
                djPatreonAccessMessage =
                    "Unlock DJ Live before checking your connection.";
            });

        ImGui.SameLine(
            0f,
            gap);

        DrawDjActionButton(
            "##openLockedDjGuide",
            FontAwesomeIcon.BookOpen,
            "Connection guide",
            new Vector2(
                smallWidth,
                actionHeight),
            false,
            () =>
            {
                djConnectionGuideOpen = true;
            });

        var primaryWidth =
            Math.Max(
                245f,
                availableWidth -
                smallWidth * 3f -
                gap * 3f);

        ImGui.SameLine(
            0f,
            gap);

        DrawDjActionButton(
            "##broadcastLockedDjToWatchParty",
            FontAwesomeIcon.Play,
            "Broadcast DJ Stream to Watch Party",
            new Vector2(
                primaryWidth,
                actionHeight),
            true,
            () => { },
            true,
            MutedText);

        ImGui.Dummy(
            UiVec(0f, 12f));

        ImGui.PushTextWrapPos(
            ImGui.GetCursorPosX() +
            availableWidth);

        ImGui.TextColored(
            MutedText,
            "After connecting to our Icecast server, press check connection. Once you see 'ON AIR' you'll then be able to stream your DJ broadcast to your watch party.");

        ImGui.PopTextWrapPos();
    }

    private void DrawDjCredentials(
        RadioCredentialsDto radio)
    {
        var availableWidth =
            ImGui.GetContentRegionAvail().X;

        var gap = NativeUi(12f);

        var columnWidth =
            (availableWidth - gap) *
            0.5f;

        DrawDjCredentialField(
            "##djServerCredential",
            "Server",
            radio.SourceHost,
            columnWidth);

        ImGui.SameLine(
            0f,
            gap);

        DrawDjCredentialField(
            "##djPortCredential",
            "Port",
            radio.SourcePort.ToString(),
            columnWidth);

        ImGui.Dummy(
            UiVec(0f, 8f));

        DrawDjCredentialField(
            "##djUsernameCredential",
            "Username",
            radio.SourceUser,
            columnWidth);

        ImGui.SameLine(
            0f,
            gap);

        DrawDjCredentialField(
            "##djMountCredential",
            "Mount",
            radio.Mount,
            columnWidth);

        ImGui.Dummy(
            UiVec(0f, 8f));

        DrawDjPasswordField(
            radio.SourcePassword ?? string.Empty,
            columnWidth);

        ImGui.SameLine(
            0f,
            gap);

        DrawDjRecommendationField(
            columnWidth);

        ImGui.Dummy(
            UiVec(0f, 12f));

        DrawDjBroadcastAllowancePlaceholder(
            availableWidth);

        ImGui.Dummy(
            UiVec(0f, 12f));

        DrawDjCredentialActions(
            radio,
            availableWidth);
    }

    private void DrawDjBroadcastAllowancePlaceholder(
        float width)
    {
        if (DrawAccountBroadcastRestriction("DJ")) return;
        var allowance = BroadcastAllowanceDisplayFor(
            BroadcastAllowanceMethod.DjLive);
        var remaining = allowance.Remaining;
        var remainingLabel = allowance.RemainingLabel;
        var previewLabel = allowance.Detail;

        using var style = ImRaii.PushStyle(
                ImGuiStyleVar.ChildRounding,
                Ui(8f))
            .Push(
                ImGuiStyleVar.ChildBorderSize,
                Ui(1f))
            .Push(
                ImGuiStyleVar.WindowPadding,
                UiVec(16f, 10f));
        using var colors = ImRaii.PushColor(
                ImGuiCol.ChildBg,
                CardBg)
            .Push(
                ImGuiCol.Border,
                BorderSubtle);
        using var panel = UiFonts.Window(ImGui.GetFontSize(), ImRaii.Child(
            "##djBroadcastAllowance",
            new Vector2(width, Ui(width < Ui(500f) ? 162f : 116f)),
            true,
            ImGuiWindowFlags.NoScrollbar |
            ImGuiWindowFlags.NoScrollWithMouse));

        if (!panel)
        {
            return;
        }

        using var uiFontScale30171=UiFonts.PushRelative(0.82f);
        var heading = System.Globalization.CultureInfo.InvariantCulture.TextInfo
            .ToTitleCase(allowance.Heading.ToLowerInvariant())
            .Replace("Weekly Broadcast Allowance", "weekly broadcast allowance");
        var headingWidth = ImGui.GetContentRegionAvail().X;
        using (ImRaii.TextWrapPos(0f))
            ImGui.TextColored(MutedText, heading);

        var remainingSize =
            ImGui.CalcTextSize(remainingLabel);
        if (ImGui.CalcTextSize(heading).X + remainingSize.X + Ui(12f) <= headingWidth)
        {
            ImGui.SameLine();
            ImGui.SetCursorPosX(ImGui.GetWindowWidth() - ImGui.GetStyle().WindowPadding.X - remainingSize.X);
        }
        ImGui.TextColored(
            MutedText,
            remainingLabel);
        uiFontScale30171.Dispose();

        var barOrigin = ImGui.GetCursorScreenPos();
        var barSize = new Vector2(
            ImGui.GetContentRegionAvail().X,
            Ui(14f));
        var fillSize = new Vector2(
            barSize.X * remaining,
            barSize.Y);
        var drawList = ImGui.GetWindowDrawList();
        ImGui.Dummy(barSize);

        drawList.AddRectFilled(
            barOrigin,
            barOrigin + barSize,
            ImGui.GetColorU32(CardBg),
            Ui(7f));
        drawList.AddRectFilled(
            barOrigin,
            barOrigin + fillSize,
            ImGui.GetColorU32(Accent),
            Ui(7f));
        drawList.PushClipRect(
            barOrigin,
            barOrigin + fillSize,
            true);
        var stripeStep = Ui(13f);
        for (var x = barOrigin.X - barSize.Y;
             x < barOrigin.X + fillSize.X + barSize.Y;
             x += stripeStep)
        {
            drawList.AddLine(
                new Vector2(x, barOrigin.Y + barSize.Y),
                new Vector2(x + barSize.Y, barOrigin.Y),
                ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 0.16f)),
                Ui(4f));
        }
        drawList.PopClipRect();
        drawList.AddRect(
            barOrigin,
            barOrigin + barSize,
            ImGui.GetColorU32(BorderSubtle),
            Ui(7f));

        using var uiFontScale32033=UiFonts.PushRelative(0.76f);
        ImGui.TextColored(
            MutedText,
            previewLabel);
        DrawPatreonUpgradeLink(ImGui.GetWindowWidth() - ImGui.GetStyle().WindowPadding.X);
        ImGui.Dummy(UiVec(0f, 4f));
        using (ImRaii.TextWrapPos(0f))
            ImGui.TextColored(MutedText,
                "Higher quality audio streams consume more allowance. To use less bandwidth, set a lower audio bitrate in your DJ software.");
        uiFontScale32033.Dispose();
    }

    private static void DrawDjCredentialField(
        string id,
        string label,
        string value,
        float width)
    {
        var height = NativeUi(48f);

        using var fieldStyle =
            ImRaii.PushStyle(
                ImGuiStyleVar.ChildRounding,
                8f)
            .Push(
                ImGuiStyleVar.WindowPadding,
                UiVec(12f, 8f));

        using var fieldColors =
            ImRaii.PushColor(
                    ImGuiCol.ChildBg,
                    FrameBg)
                .Push(
                    ImGuiCol.Border,
                    BorderSubtle);

        if (UiFonts.Window(ImGui.GetFontSize(), ImGui.BeginChild(
                id,
                new Vector2(
                    width,
                    height),
                true,
                ImGuiWindowFlags.NoScrollbar |
                ImGuiWindowFlags.NoScrollWithMouse)))
        {
            ImGui.TextColored(
                MutedText,
                label);

            ImGui.SameLine(
                0f,
                16f);

            var copySize = UiVec(30f, 28f);
            var copyX = ImGui.GetWindowWidth() - copySize.X - Ui(8f);
            var valueWidth = MathF.Max(0f, copyX - Ui(8f) - ImGui.GetCursorPosX());
            var displayValue = TruncateToWidth(value, valueWidth);
            ImGui.TextColored(Hex(0xE2E7EF), displayValue);
            if (displayValue != value && ImGui.IsItemHovered())
                UiFonts.SetTooltip(value);

            ImGui.SameLine();

            ImGui.SetCursorPosX(copyX);

            DrawDjCopyButton($"##copy{id}", value, copySize);

        }

        ImGui.EndChild();
    }

    private static void DrawDjCopyButton(string id, string value, Vector2 size)
    {
        var origin = ImGui.GetCursorScreenPos();
        if (ImGui.InvisibleButton(id, size))
            ImGui.SetClipboardText(value);
        var hovered = ImGui.IsItemHovered();
        var color = ImGui.IsItemActive() ? AccentActive : hovered ? AccentHover : Vector4.One;
        using (UiFonts.PushIcon())
        {
            var glyph = FontAwesomeIcon.Clipboard.ToIconString();
            var glyphSize = ImGui.CalcTextSize(glyph);
            UiFonts.DrawText(ImGui.GetWindowDrawList(), ImGui.GetFontSize(), origin + (size - glyphSize) * 0.5f, ImGui.GetColorU32(color), glyph);
        }
        if (hovered)
            UiFonts.SetTooltip("Copy to clipboard");
    }

    private void DrawDjPasswordField(
        string password,
        float width)
    {
        var height = NativeUi(48f);

        using var fieldStyle =
            ImRaii.PushStyle(
                ImGuiStyleVar.ChildRounding,
                8f)
            .Push(
                ImGuiStyleVar.WindowPadding,
                UiVec(12f, 8f));

        using var fieldColors =
            ImRaii.PushColor(
                    ImGuiCol.ChildBg,
                    FrameBg)
                .Push(
                    ImGuiCol.Border,
                    BorderSubtle);

        if (UiFonts.Window(ImGui.GetFontSize(), ImGui.BeginChild(
                "##djPasswordCredential",
                new Vector2(
                    width,
                    height),
                true,
                ImGuiWindowFlags.NoScrollbar |
                ImGuiWindowFlags.NoScrollWithMouse)))
        {
            var displayedPassword =
                djPasswordVisible
                    ? password
                    : new string(
                        '•',
                        Math.Clamp(
                            password.Length,
                            8,
                            16));

            ImGui.TextColored(
                MutedText,
                "Password");

            ImGui.SameLine(
                0f,
                16f);

            ImGui.TextColored(
                Hex(0xE2E7EF),
                displayedPassword);

            var buttonSize =
                UiVec(30f, 28f);

            ImGui.SameLine();

            ImGui.SetCursorPosX(
                ImGui.GetWindowWidth() -
                buttonSize.X * 2f -
                12f);

            using (UiFonts.PushIcon())
            using (ImRaii.PushColor(
                       ImGuiCol.Button,
                       Vector4.Zero)
                       .Push(
                           ImGuiCol.ButtonHovered,
                           FrameBgHover)
                       .Push(
                           ImGuiCol.ButtonActive,
                           AccentActive))
            {
                if (ImGui.Button(
                        $"{(djPasswordVisible ? FontAwesomeIcon.EyeSlash : FontAwesomeIcon.Eye).ToIconString()}##showDjPassword",
                        buttonSize))
                {
                    djPasswordVisible =
                        !djPasswordVisible;
                }

                ImGui.SameLine(
                    0f,
                    2f);

                DrawDjCopyButton("##copyDjPassword", password, buttonSize);
            }
        }

        ImGui.EndChild();
    }

    private static void DrawDjRecommendationField(
        float width)
    {
        var height = NativeUi(48f);

        using var fieldStyle =
            ImRaii.PushStyle(
                ImGuiStyleVar.ChildRounding,
                8f)
            .Push(
                ImGuiStyleVar.WindowPadding,
                UiVec(12f, 8f));

        using var fieldColors =
            ImRaii.PushColor(
                    ImGuiCol.ChildBg,
                    new Vector4(
                        Accent.X * 0.10f,
                        Accent.Y * 0.10f,
                        Accent.Z * 0.16f,
                        0.95f))
                .Push(
                    ImGuiCol.Border,
                    new Vector4(
                        Accent.X,
                        Accent.Y,
                        Accent.Z,
                        0.32f));

        if (UiFonts.Window(ImGui.GetFontSize(), ImGui.BeginChild(
                "##djRecommendations",
                new Vector2(
                    width,
                    height),
                true,
                ImGuiWindowFlags.NoScrollbar |
                ImGuiWindowFlags.NoScrollWithMouse)))
        {
            ImGui.TextColored(
                AccentHover,
                "Recommended");

            ImGui.SameLine(
                0f,
                12f);

            ImGui.TextColored(
                MutedText,
                "MP3, OGG or AAC  •  128 kbps  •  Stereo  •  44.1/48 kHz");
        }

        ImGui.EndChild();
    }

    private void DrawDjCredentialActions(
     RadioCredentialsDto radio,
     float availableWidth)
    {
        var gap = NativeUi(9f);
        var smallWidth = Ui(170f);
        var actionHeight = Ui(38f);
        var actionRowX = ImGui.GetCursorPosX();

        var viewingAnotherParty =
            stream.Mode == StreamMode.Viewing;

        DrawDjActionButton(
            "##copyAllDjSettings",
            FontAwesomeIcon.Clipboard,
            "Copy all settings",
            new Vector2(
                smallWidth,
                actionHeight),
            false,
            () =>
            {
                ImGui.SetClipboardText(
                    BuildDjCredentialClipboard(
                        radio));
            });

        ImGui.SameLine(
            0f,
            gap);

        DrawDjActionButton(
       "##checkDjConnection",
       FontAwesomeIcon.BroadcastTower,
       djConnectionSearchActive
           ? "Checking..."
           : "Check connection",
new Vector2(
    smallWidth,
    actionHeight),
false,
RequestDjConnectionCheck,
false,
djConnectionSearchActive
    ? Gold
    : Vector4.One);

        var flashRemaining = (djConnectionCheckFlashUntilUtc - DateTime.UtcNow).TotalSeconds;
        if (flashRemaining > 0)
        {
            var alpha = 0.45f + 0.55f * (float)Math.Pow(Math.Sin(flashRemaining * Math.PI * 3), 2);
            ImGui.GetWindowDrawList().AddRect(ImGui.GetItemRectMin(), ImGui.GetItemRectMax(),
                ImGui.GetColorU32(new Vector4(Gold.X, Gold.Y, Gold.Z, alpha)), Ui(7f), ImDrawFlags.None, Ui(2f));
        }

        ImGui.SameLine(
            0f,
            gap);

        DrawDjActionButton(
            "##openDjGuide",
            FontAwesomeIcon.BookOpen,
            "Connection guide",
            new Vector2(
                smallWidth,
                actionHeight),
            false,
            () =>
            {
                djConnectionGuideOpen =
                    true;
            });

        var primaryWidth =
            Math.Max(
                245f,
                availableWidth -
                smallWidth * 3f -
                gap * 3f);

        ImGui.SameLine(
            0f,
            gap);

        DrawDjActionButton(
            "##broadcastDjToWatchParty",
            FontAwesomeIcon.Play,
            "Broadcast DJ Stream to Watch Party",
            new Vector2(
                primaryWidth,
                actionHeight),
            !djIsOnAir ||
            viewingAnotherParty,
            () =>
            {
                BeginDjWatchPartyBroadcast(
                    radio);
            },
            true);

        if (!djIsOnAir && !viewingAnotherParty &&
            ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
        {
            UiFonts.SetTooltip("Press check connection first");
            if (ImGui.IsMouseClicked(ImGuiMouseButton.Left))
                djConnectionCheckFlashUntilUtc = DateTime.UtcNow.AddSeconds(1.5);
        }

        if (djBroadcastingToWatchParty &&
            stream.Mode == StreamMode.Hosting &&
            video.IsAudioOnly &&
            queue.Current is { Source: "Music / DJ" } currentDjAudio)
        {
            ImGui.Dummy(UiVec(0f, 8f));
            ImGui.SetCursorPosX(actionRowX + availableWidth - primaryWidth);
            DrawDjActionButton(
                "##djAudioPresentationSettings",
                FontAwesomeIcon.Cog,
                "Adjust visualizer / on-screen text",
                new Vector2(primaryWidth, actionHeight),
                false,
                () => OpenAudioPresentationSettings(currentDjAudio));
        }

        ImGui.Dummy(
            UiVec(0f, 12f));

        if (viewingAnotherParty)
        {
            ImGui.TextColored(
                MutedText,
                "Leave your current Watch Party before broadcasting your DJ stream.");
        }
        else if (djBroadcastingToWatchParty &&
                 stream.Mode == StreamMode.Hosting)
        {
            ImGui.TextColored(
                new Vector4(
                    0.25f,
                    0.90f,
                    0.48f,
                    1f),
                "Currently broadcasting DJ stream to watch party");
        }
        else
        {
            ImGui.PushTextWrapPos(
                ImGui.GetCursorPosX() +
                availableWidth);

            ImGui.TextColored(
                MutedText,
                "After connecting to our Icecast server, press check connection. Once you see 'ON AIR' you'll then be able to stream your DJ broadcast to your watch party.");

            ImGui.PopTextWrapPos();
        }
    }

    private void BeginDjWatchPartyBroadcast(
    RadioCredentialsDto radio)
    {
        if (!djIsOnAir)
        {
            return;
        }

        if (stream.Mode == StreamMode.Viewing)
        {
            Plugin.ChatGui.Print(
                "[AlphaChannel] Leave your current Watch Party before broadcasting your DJ stream.");

            return;
        }

        if (stream.Mode == StreamMode.Hosting)
        {
            StartDjBroadcastInHostedRoom(
                radio);

            return;
        }

        pendingWatchPartyMediaKind =
            PendingWatchPartyMediaKind.DjBroadcast;

        watchPartyCreationPopupOpen =
            true;

        createRoomPassword =
            string.Empty;

        createLockedRoomPasswordError =
            null;
    }

    private void StartDjBroadcastInHostedRoom(
       RadioCredentialsDto radio,
       bool navigateToWatchParty = false)
    {
        if (!CanStartBroadcastWithAllowance(BroadcastAllowanceMethod.DjLive))
        {
            return;
        }

        //
        // DJ streams are intended to be heard directly rather than through
        // the in-game TV. Mute the local host TV when broadcasting begins.
        //
        Plugin.Cfg.Muted =
            true;

        // Keep the decoded audio at the configured level so it can continue to
        // drive both Classic Bars and the FFmpeg visualizers. The local output
        // is muted separately after playback starts to avoid hearing the
        // delayed Icecast return feed.
        video.SetVolume(
            Plugin.Cfg.Volume);

        Plugin.Cfg.Save();

        djAutoMuteNoticeVisible =
            true;

        djAutoMutedStreamUrl =
            radio.ListenUrl;

        PlayDjUrl(
            radio.ListenUrl,
            DjBroadcastContentTitle,
            "Music / DJ");

        video.SetOutputMuted(
            true);

        djBroadcastingToWatchParty =
            true;

        if (navigateToWatchParty)
        {
            currentPage =
                HomePage.WatchAlong;

            partyPanelTab =
                PartyPanelTab.NowPlaying;
        }

    }

    private void CompletePendingWatchPartyMedia()
    {
        if (pendingWatchPartyMediaKind ==
            PendingWatchPartyMediaKind.None)
        {
            return;
        }

        var pendingKind =
            pendingWatchPartyMediaKind;

        pendingWatchPartyMediaKind =
            PendingWatchPartyMediaKind.None;

        watchPartyCreationPopupOpen =
            false;

        switch (pendingKind)
        {
            case PendingWatchPartyMediaKind.TwitchRoom:
                // Create the room first; sharing the Twitch stream remains a separate action.
                break;
            case PendingWatchPartyMediaKind.LocalVideoRoom:
                // Keep the video local and stay on its page until settings are confirmed.
                break;
            case PendingWatchPartyMediaKind.GameRoom:
                currentPage =
                    HomePage.WatchAlong;

                partyPanelTab =
                    PartyPanelTab.NowPlaying;
                break;

            case PendingWatchPartyMediaKind.Whiteboard:
                currentPage =
                    HomePage.Whiteboard;

                StartHostWhiteboard(pendingWhiteboardMode);
                break;

            case PendingWatchPartyMediaKind.Quiz:
                currentPage =
                    HomePage.Quiz;

                StartQuizLoad(pendingQuizQuickPlay);
                break;

            case PendingWatchPartyMediaKind.Pictionary:
                currentPage =
                    HomePage.Pictionary;

                lastActivitiesPage =
                    HomePage.Pictionary;

                pictionaryShowSetup = true;
                if (CanStartPictionaryFromSetup())
                {
                    StartPictionaryGame();
                }
                break;

            case PendingWatchPartyMediaKind.Roulette:
                currentPage = HomePage.Roulette;
                lastActivitiesPage = HomePage.Roulette;
                StartRouletteGame();
                break;

            case PendingWatchPartyMediaKind.Bingo:
                currentPage = HomePage.Bingo;
                lastActivitiesPage = HomePage.Bingo;
                StartBingoGame();
                break;

            case PendingWatchPartyMediaKind.BlastParty:
                currentPage = lastActivitiesPage = HomePage.BlastParty;
                StartBlastParty();
                break;
            case PendingWatchPartyMediaKind.SkyRail:
                currentPage = HomePage.SkyRail;
                lastActivitiesPage = HomePage.SkyRail;
                StartSkyRailGame();
                break;
            case PendingWatchPartyMediaKind.Pixels:
                currentPage = lastActivitiesPage = HomePage.Pixels;
                StartPixels();
                break;
            case PendingWatchPartyMediaKind.LuckyWheel:
                currentPage = lastActivitiesPage = HomePage.LuckyWheel;
                StartLuckyWheel();
                break;
            case PendingWatchPartyMediaKind.SlotMachine:
                currentPage = HomePage.SlotMachine;
                lastActivitiesPage = HomePage.SlotMachine;
                StartSlotMachineGame();
                break;
            case PendingWatchPartyMediaKind.Poker:
                currentPage = HomePage.Poker;
                lastActivitiesPage = HomePage.Poker;
                StartPokerTable();
                break;

            case PendingWatchPartyMediaKind.ChocoRace:
                currentPage = HomePage.RacePreview;
                lastActivitiesPage = HomePage.RacePreview;
                StartChocoRace();
                break;

            case PendingWatchPartyMediaKind.DjBroadcast:
                if (radioCredentials is { } radio)
                {
                    StartDjBroadcastInHostedRoom(
                        radio,
                        navigateToWatchParty: true);
                }

                break;

            case PendingWatchPartyMediaKind.LiveStreamBroadcast:
                StartLiveBroadcastInHostedRoom(
                    navigateToWatchParty: true);

                break;

            case PendingWatchPartyMediaKind.StudioBroadcast:
                StartStudioBroadcast();
                break;

            case PendingWatchPartyMediaKind.LocalVideoStartAndBroadcast:
                StartLocalVideoBroadcastInHostedRoom(
                    startPlayback: true,
                    navigateToWatchParty: true);

                break;

            case PendingWatchPartyMediaKind.LocalVideoCurrentPlaybackBroadcast:
                StartLocalVideoBroadcastInHostedRoom(
                    startPlayback: false,
                    navigateToWatchParty: true);

                break;

            case PendingWatchPartyMediaKind.WebLink:
                if (pendingWatchPartyVideoEntry is { } videoEntry)
                {
                    HandlePlayNow(
                        videoEntry);

                    currentPage =
                        HomePage.WatchAlong;

                    partyPanelTab =
                        PartyPanelTab.NowPlaying;
                }

                break;

            case PendingWatchPartyMediaKind.RadioStation:
                if (!string.IsNullOrWhiteSpace(
                        pendingWatchPartyRadioUrl))
                {
                    PlayDjUrl(
                        pendingWatchPartyRadioUrl,
                        pendingWatchPartyRadioTitle ??
                        "Radio station",
                        "Radio");

                    currentPage =
                        HomePage.WatchAlong;

                    partyPanelTab =
                        PartyPanelTab.NowPlaying;
                }

                break;
        }

        ClearPendingWatchPartySelection();
    }

    private void CancelPendingWatchPartyMedia()
    {
        if (spotifyOwnsStudio && pendingWatchPartyMediaKind == PendingWatchPartyMediaKind.StudioBroadcast)
            StopStudioBroadcast();
        watchPartyCreationPopupOpen =
            false;

        pendingWatchPartyMediaKind =
            PendingWatchPartyMediaKind.None;

        ClearPendingWatchPartySelection();

        createRoomPassword =
            string.Empty;

        createLockedRoomPasswordError =
            null;
    }

    private void ClearPendingWatchPartySelection()
    {
        pendingWatchPartyVideoEntry =
            null;

        pendingWatchPartyRadioUrl =
            null;

        pendingWatchPartyRadioTitle =
            null;

        pendingQuizQuickPlay =
            false;
    }

    private void BeginWebLinkWatchParty(
        Video.VideoQueueEntry entry)
    {
        if (ChoosePageVideo(entry, BeginWebLinkWatchParty)) return;
        if (stream.Mode != StreamMode.None)
        {
            return;
        }

        ClearPendingWatchPartySelection();

        pendingWatchPartyVideoEntry =
            entry;

        pendingWatchPartyMediaKind =
            PendingWatchPartyMediaKind.WebLink;

        OpenPendingWatchPartyCreation();
    }

    private void BeginRadioStationWatchParty(
        string url,
        string title)
    {
        if (stream.Mode != StreamMode.None ||
            string.IsNullOrWhiteSpace(url))
        {
            return;
        }

        ClearPendingWatchPartySelection();

        pendingWatchPartyRadioUrl =
            url;

        pendingWatchPartyRadioTitle =
            title;

        pendingWatchPartyMediaKind =
            PendingWatchPartyMediaKind.RadioStation;

        OpenPendingWatchPartyCreation();
    }

    private void OpenPendingWatchPartyCreation()
    {
        watchPartyCreationPopupOpen =
            true;

        createRoomPassword =
            string.Empty;

        createLockedRoomPasswordError =
            null;
    }

    private static string BuildDjCredentialClipboard(
        RadioCredentialsDto radio)
    {
        return
            $"Server: {radio.SourceHost}{Environment.NewLine}" +
            $"Port: {radio.SourcePort}{Environment.NewLine}" +
            $"Username: {radio.SourceUser}{Environment.NewLine}" +
            $"Mount: {radio.Mount}{Environment.NewLine}" +
            $"Password: {radio.SourcePassword ?? string.Empty}{Environment.NewLine}" +
            "Encrypted alternative: port 8443 with SSL/TLS enabled.";
    }

    private static void DrawDjActionButton(
    string id,
    FontAwesomeIcon icon,
    string label,
    Vector2 size,
    bool disabled,
    Action action,
    bool primary = false,
    Vector4? textColor = null)
    {
        using (UiDisabled.Scope(
                   disabled))
        using (ImRaii.PushColor(
                   ImGuiCol.Button,
                   primary
                       ? Accent
                       : FrameBg)
                   .Push(
                       ImGuiCol.ButtonHovered,
                       primary
                           ? AccentHover
                           : FrameBgHover)
                   .Push(
                       ImGuiCol.ButtonActive,
                       AccentActive))
        {
            var origin =
                ImGui.GetCursorScreenPos();

            if (ImGui.Button(
                    id,
                    size))
            {
                action();
            }

            DrawPlayerActionButtonContent(
                origin,
                size,
                icon,
                label,
                textColor ??
                Vector4.One);
        }
    }

    private static void DrawDjOrDivider()
    {
        var width =
            ImGui.GetContentRegionAvail().X;

        const string label =
            "OR";

        var textSize =
            ImGui.CalcTextSize(
                label);

        var centerX =
            ImGui.GetCursorScreenPos().X +
            width * 0.5f;

        var y =
            ImGui.GetCursorScreenPos().Y +
            textSize.Y * 0.5f;

        var drawList =
            ImGui.GetWindowDrawList();

        drawList.AddLine(
            new Vector2(
                ImGui.GetCursorScreenPos().X,
                y),
            new Vector2(
                centerX -
                textSize.X -
                Ui(16f),
                y),
            ImGui.GetColorU32(
                BorderSubtle),
            1f);

        drawList.AddLine(
            new Vector2(
                centerX +
                textSize.X +
                Ui(16f),
                y),
            new Vector2(
                ImGui.GetCursorScreenPos().X +
                width,
                y),
            ImGui.GetColorU32(
                BorderSubtle),
            1f);

        ImGui.SetCursorPosX(
            ImGui.GetCursorPosX() +
            (width - textSize.X) * 0.5f);

        ImGui.TextColored(
            MutedText,
            label);
    }

    private void DrawDjRadioSection()
    {
        var height =
            610f +
            djSavedStations.Count *
            60f;

        using var sectionStyle =
            ImRaii.PushStyle(
                    ImGuiStyleVar.ChildRounding,
                    12f)
                .Push(
                    ImGuiStyleVar.ChildBorderSize,
                    1f)
                .Push(
                    ImGuiStyleVar.WindowPadding,
                    UiVec(18f, 16f));

        using var sectionColors =
            ImRaii.PushColor(
                    ImGuiCol.ChildBg,
                    CardBg)
                .Push(
                    ImGuiCol.Border,
                    BorderSubtle);

        if (UiFonts.Window(ImGui.GetFontSize(), ImGui.BeginChild(
                "##djRadioSection",
                new Vector2(
                    -1f,
                    height),
                true,
                ImGuiWindowFlags.NoScrollbar |
                ImGuiWindowFlags.NoScrollWithMouse)))
        {
            DrawDjRadioPanel(
                ImGui.GetContentRegionAvail().X);
        }

        ImGui.EndChild();
    }

    //
    // Periodically checks whether Icecast currently has an active
    // source connected to this account's mount.
    //

    private void UpdateDjConnectionStatus()
    {
        if (radioCredentials is null ||
            djConnectionCheckInProgress)
        {
            return;
        }

        var now =
            DateTime.UtcNow;

        //
        // An explicit connection search runs for a maximum of
        // three minutes and checks every eight seconds.
        //
        if (djConnectionSearchActive)
        {
            if (now >=
                djConnectionSearchEndsUtc)
            {
                djConnectionSearchActive =
                    false;

                djNextConnectionCheckUtc =
                    DateTime.MaxValue;

                return;
            }

            if (now >=
                djNextConnectionCheckUtc)
            {
                PerformDjConnectionCheck();
            }

            return;
        }

        //
        // Once a connection has been confirmed, perform only one
        // passive health check every 60 seconds.
        //
        if (djConnectionMonitoringEnabled &&
            now >=
            djNextConnectionCheckUtc)
        {
            PerformDjConnectionCheck();
        }
    }

    private void RequestDjConnectionCheck()
{
    if (radioCredentials is null)
    {
        return;
    }

    //
    // Pressing the button begins or restarts the
    // three-minute connection search.
    //
    djConnectionSearchActive =
        true;

    djConnectionSearchEndsUtc =
        DateTime.UtcNow.AddMinutes(
            3);

    djNextConnectionCheckUtc =
        DateTime.MinValue;

    //
    // Start the first check immediately.
    //
    if (!djConnectionCheckInProgress)
    {
        PerformDjConnectionCheck();
    }
}

    private void PerformDjConnectionCheck()
    {
        if (radioCredentials is not { } radio ||
            djConnectionCheckInProgress)
        {
            return;
        }

        djConnectionCheckInProgress =
            true;

        var listenUrl =
            radio.ListenUrl;

        _ = Task.Run(
            async () =>
            {
                bool isOnAir;

                try
                {
                    isOnAir =
                        await radioClient
                            .IsOnAirAsync(
                                listenUrl)
                            .ConfigureAwait(false);
                }
                catch (Exception)
                {
                    AepLog.Warning(
                        "[Radio] connection check failed.");

                    isOnAir =
                        false;
                }

                var now =
                    DateTime.UtcNow;

                var wasOnAir =
                    djIsOnAir;

                djIsOnAir =
                    isOnAir;

                if (isOnAir)
                {
                    if (!wasOnAir ||
                        djOnAirSinceUtc is null)
                    {
                        djOnAirSinceUtc =
                            now;
                    }

                    //
                    // A successful result ends the frequent search and enables
                    // low-frequency health monitoring.
                    //
                    djConnectionSearchActive =
                        false;

                    djConnectionMonitoringEnabled =
                        true;

                    djNextConnectionCheckUtc =
                        now.AddSeconds(
                            60);
                }
                else
                {
                    djOnAirSinceUtc =
                        null;

                    if (djConnectionSearchActive &&
                        now <
                        djConnectionSearchEndsUtc)
                    {
                        //
                        // During the explicit three-minute search, retry using
                        // the existing eight-second polling frequency.
                        //
                        djNextConnectionCheckUtc =
                            now.AddSeconds(
                                8);
                    }
                    else if (djConnectionMonitoringEnabled)
                    {
                        //
                        // A previously confirmed connection is checked only
                        // once per minute, even after it goes offline.
                        //
                        djNextConnectionCheckUtc =
                            now.AddSeconds(
                                60);
                    }
                    else
                    {
                        djConnectionSearchActive =
                            false;

                        djNextConnectionCheckUtc =
                            DateTime.MaxValue;
                    }
                }

                djConnectionCheckInProgress =
                    false;
            });
    }

    private static string FormatDjOnAirDuration(
        TimeSpan duration)
    {
        if (duration < TimeSpan.Zero)
        {
            duration =
                TimeSpan.Zero;
        }

        if (duration.TotalHours >= 1d)
        {
            return
                $"{(int)duration.TotalHours:00}:{duration.Minutes:00}:{duration.Seconds:00}";
        }

        return
            $"{duration.Minutes:00}:{duration.Seconds:00}";
    }


    private void DrawDjSupportedFormatsBox(
        float width)
    {
        var height = NativeUi(72f);

        using (
            ImRaii.PushStyle(
                ImGuiStyleVar.ChildRounding,
                9f))
        using (
            ImRaii.PushColor(
                ImGuiCol.ChildBg,
                new Vector4(
                    Accent.X * 0.14f,
                    Accent.Y * 0.12f,
                    Accent.Z * 0.20f,
                    0.92f)))
        using (
            ImRaii.PushColor(
                ImGuiCol.Border,
                new Vector4(
                    Accent.X,
                    Accent.Y,
                    Accent.Z,
                    0.22f)))
        using (
            var box =
                UiFonts.Window(ImGui.GetFontSize(), ImRaii.Child(
                    "##djSupportedFormats",
                    new Vector2(
                        width,
                        height),
                    true,
                    ImGuiWindowFlags.NoScrollbar |
                    ImGuiWindowFlags.NoScrollWithMouse)))
        {
            if (!box)
            {
                return;
            }

            var start =
                ImGui.GetCursorScreenPos();

            var drawList =
                ImGui.GetWindowDrawList();


            //
            // Icon
            //

            var iconCenter =
                start +
                UiVec(22f, 36f);

            drawList.AddCircleFilled(
                iconCenter,
                14f,
                ImGui.GetColorU32(
                    new Vector4(
                        Accent.X,
                        Accent.Y,
                        Accent.Z,
                        0.18f)),
                24);

            using (
                UiFonts.PushIcon())
            {
                var glyph =
                    FontAwesomeIcon.Lightbulb
                        .ToIconString();

                var glyphSize =
                    ImGui.CalcTextSize(
                        glyph);

                UiFonts.DrawText(drawList, ImGui.GetFontSize(), iconCenter -
                    glyphSize / 2f, ImGui.GetColorU32(
                        Accent), glyph);
            }


            //
            // Copy
            //

            ImGui.SetCursorPos(
               UiVec(48f, 10f));

            using var uiFontScale72085=UiFonts.PushRelative(0.86f);

            ImGui.TextColored(
                Vector4.One,
                "Supported formats");

            uiFontScale72085.Dispose();


            ImGui.SetCursorPos(
        UiVec(48f, 34f));

            using var uiFontScale72361=UiFonts.PushRelative(0.78f);

            ImGui.TextColored(
                MutedText,
                "MP3, AAC, OGG, M4A, WAV and most live audio streams.");

            uiFontScale72361.Dispose();
        }
    }


    // ---------------------------------------------------------
    // Radio stations
    // ---------------------------------------------------------

    private void DrawDjRadioPanel(
       float width)
    {
        using var group =
            ImRaii.Group();

        var headerPosition = ImGui.GetCursorScreenPos();

        using var uiFontScale72961=UiFonts.PushRelative(1.05f);

        ImGui.TextColored(
            Vector4.One,
            "Listen to a radio station");

        uiFontScale72961.Dispose();

        ImGui.Dummy(
            UiVec(0f, 3f));

        using var uiFontScale73206=UiFonts.PushRelative(0.82f);

        ImGui.TextColored(
            MutedText,
            "Play a suggested station or save your own direct stream.");

        uiFontScale73206.Dispose();

        if (stream.Mode != StreamMode.Viewing &&
            video.IsAudioOnly &&
            queue.Current is { Source: "Radio" } currentRadio)
        {
            var afterDescription = ImGui.GetCursorScreenPos();
            var buttonWidth = MathF.Min(Ui(292f), width * 0.46f);
            ImGui.SetCursorScreenPos(new Vector2(
                headerPosition.X + width - buttonWidth,
                headerPosition.Y));
            DrawDjActionButton(
                "##radioAudioPresentationSettings",
                FontAwesomeIcon.Cog,
                "Adjust visualizer / on-screen text",
                new Vector2(buttonWidth, Ui(34f)),
                false,
                () => OpenAudioPresentationSettings(currentRadio));
            ImGui.SetCursorScreenPos(afterDescription);
        }

        ImGui.Dummy(
            UiVec(0f, 7f));

        using var uiFontScale74309=UiFonts.PushRelative(0.78f);

        ImGui.TextColored(
            MutedText,
            "SUGGESTED STATIONS");

        uiFontScale74309.Dispose();

        ImGui.Dummy(
            UiVec(0f, 4f));

        for (var index = 0; index < SuggestedRadioStations.Length; index++)
        {
            var station = SuggestedRadioStations[index];
            DrawDjFeaturedStation($"##djFeatured{index}", station.Icon,
                station.Name, station.Description, station.Url, width);
            if (index < SuggestedRadioStations.Length - 1)
                ImGui.Dummy(UiVec(0f, 3f));
        }

        ImGui.Dummy(
            UiVec(0f, 8f));

        //
        // Saved stations header and Add button.
        //

        var savedHeaderPosition =
            ImGui.GetCursorScreenPos();

        using var uiFontScale75162=UiFonts.PushRelative(0.78f);

        ImGui.TextColored(
            MutedText,
            "YOUR STATIONS");

        uiFontScale75162.Dispose();

        var addButtonSize =
            UiVec(122f, 30f);

        ImGui.SetCursorScreenPos(
            new Vector2(
                savedHeaderPosition.X +
                width -
                addButtonSize.X,
                savedHeaderPosition.Y -
                Ui(6f)));

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
            var buttonOrigin =
                ImGui.GetCursorScreenPos();

            if (ImGui.Button(
                    "##openAddDjStation",
                    addButtonSize))
            {
                OpenDjStationEditor(
                    null);
            }

            DrawPlayerActionButtonContent(
                buttonOrigin,
                addButtonSize,
                FontAwesomeIcon.Plus,
                "Add station",
                Vector4.One);
        }

        ImGui.SetCursorScreenPos(
            new Vector2(
                savedHeaderPosition.X,
                savedHeaderPosition.Y + Ui(25f)));

        if (djSavedStations.Count == 0)
        {
            ImGui.TextColored(
                MutedText,
                "No saved stations yet.");
        }
        else
        {
            for (var index = 0;
                 index < djSavedStations.Count;
                 index++)
            {
                DrawDjSavedStation(
                    djSavedStations[index],
                    index,
                    width);

                ImGui.Dummy(
                    UiVec(0f, 4f));
            }
        }
    }


    private void DrawDjFeaturedStation(
    string id,
    FontAwesomeIcon icon,
    string name,
    string description,
    string url,
    float width)
    {
        var height = NativeUi(42f);

        var origin =
            ImGui.GetCursorScreenPos();

        var drawList =
            ImGui.GetWindowDrawList();

        var min =
            origin;

        var max =
            origin +
            new Vector2(
                width,
                height);

        drawList.AddRectFilled(
            min,
            max,
            ImGui.GetColorU32(
                FrameBg),
            8f);

        drawList.AddRect(
            min,
            max,
            ImGui.GetColorU32(
                BorderSubtle),
            8f);

        //
        // Icon tile
        //

        var tileMin =
            min +
            UiVec(5f, 5f);

        var tileMax =
            tileMin +
            UiVec(32f, 32f);

        drawList.AddRectFilled(
            tileMin,
            tileMax,
            ImGui.GetColorU32(
                new Vector4(
                    Accent.X,
                    Accent.Y,
                    Accent.Z,
                    0.22f)),
            7f);

        using (UiFonts.PushIcon())
        {
            var glyph =
                icon.ToIconString();

            var glyphSize =
                ImGui.CalcTextSize(
                    glyph);

            UiFonts.DrawText(drawList, ImGui.GetFontSize(), tileMin +
                (tileMax - tileMin) /
                2f -
                glyphSize /
                2f, ImGui.GetColorU32(
                    Accent), glyph);
        }

        //
        // Station name and description
        //

        UiFonts.DrawText(drawList, ImGui.GetFontSize(), min +
            UiVec(45f, 5f), ImGui.GetColorU32(
                Vector4.One), name);

        UiFonts.DrawText(drawList, ImGui.GetFontSize(), min +
            UiVec(45f, 23f), ImGui.GetColorU32(
                MutedText), description);

        //
        // Working Play button
        //

        var playWidth = Ui(58f);

        var partyWidth = Ui(148f);

        var buttonGap = Ui(6f);

        var showPartyAction =
            stream.Mode == StreamMode.None;

        ImGui.SetCursorScreenPos(
            new Vector2(
                max.X -
                (showPartyAction
                    ? partyWidth + buttonGap
                    : 0f) -
                playWidth -
                Ui(6f),
                min.Y +
                Ui(6f)));

        using (ImRaii.PushStyle(
                   ImGuiStyleVar.FrameRounding,
                   7f))
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
                    $"Play{id}",
                    new Vector2(
                        playWidth,
                        Ui(29f))))
            {
                PlayDjUrl(
                    url,
                    name,
                    "Radio");
            }
        }

        if (ImGui.IsItemHovered())
        {
            UiFonts.SetTooltip(
                url);
        }

        if (showPartyAction)
        {
            ImGui.SetCursorScreenPos(
                new Vector2(
                    max.X -
                    partyWidth -
                    Ui(6f),
                    min.Y +
                    Ui(6f)));

            using (ImRaii.PushStyle(
                       ImGuiStyleVar.FrameRounding,
                       7f))
            using (ImRaii.PushColor(
                       ImGuiCol.Button,
                       FrameBgHover)
                   .Push(
                       ImGuiCol.ButtonHovered,
                       Accent)
                   .Push(
                       ImGuiCol.ButtonActive,
                       AccentActive))
            {
                if (ImGui.Button(
                        $"Play in Watch Party{id}Party",
                        new Vector2(
                            partyWidth,
                            Ui(29f))))
                {
                    BeginRadioStationWatchParty(
                        url,
                        name);
                }
            }
        }

        ImGui.SetCursorScreenPos(
            new Vector2(
                min.X,
                max.Y));

        ImGui.Dummy(
            new Vector2(
                width,
                1f));
    }

    private void DrawDjSavedStation(
     SavedRadioStationRecord station,
     int index,
     float width)
    {
        var height = NativeUi(44f);

        var origin =
            ImGui.GetCursorScreenPos();

        var maximum =
            origin +
            new Vector2(
                width,
                height);

        var drawList =
            ImGui.GetWindowDrawList();

        drawList.AddRectFilled(
            origin,
            maximum,
            ImGui.GetColorU32(
                FrameBg),
            8f);

        drawList.AddRect(
            origin,
            maximum,
            ImGui.GetColorU32(
                BorderSubtle),
            8f);

        drawList.AddCircleFilled(
            origin +
            new Vector2(
                Ui(14f),
                height * 0.5f),
            4f,
            ImGui.GetColorU32(
                Good),
            16);

        UiFonts.DrawText(drawList, ImGui.GetFontSize(), origin +
            UiVec(27f, 7f), ImGui.GetColorU32(
                Vector4.One), station.Name);

        var displayedUrl =
            station.Url.Length > 70
                ? $"{station.Url[..69]}…"
                : station.Url;

        UiFonts.DrawText(drawList, ImGui.GetFontSize(), origin +
            UiVec(27f, 25f), ImGui.GetColorU32(
                MutedText), displayedUrl);

        var menuWidth = Ui(34f);

        var playWidth = Ui(62f);

        var partyWidth = Ui(148f);

        var buttonGap = Ui(6f);

        var showPartyAction =
            stream.Mode == StreamMode.None;

        ImGui.SetCursorScreenPos(
            new Vector2(
                maximum.X -
                menuWidth -
                Ui(7f),
                origin.Y + Ui(7f)));

        using (UiFonts.PushIcon())
        using (ImRaii.PushColor(
                   ImGuiCol.Button,
                   Vector4.Zero)
                   .Push(
                       ImGuiCol.ButtonHovered,
                       FrameBgHover)
                   .Push(
                       ImGuiCol.ButtonActive,
                       AccentActive))
        {
            if (ImGui.Button(
                    $"{FontAwesomeIcon.EllipsisH.ToIconString()}##djStationMenu{index}",
                    new Vector2(
                        menuWidth,
                        Ui(30f))))
            {
                ImGui.OpenPopup(
                    $"##djSavedStationActions{index}");
            }
        }

        using (ImRaii.PushStyle(
                   ImGuiStyleVar.PopupRounding,
                   8f)
                   .Push(
                       ImGuiStyleVar.WindowPadding,
                       UiVec(8f, 8f)))
        using (ImRaii.PushColor(
                   ImGuiCol.PopupBg,
                   CardBg)
                   .Push(
                       ImGuiCol.Border,
                       new Vector4(
                           Accent.X,
                           Accent.Y,
                           Accent.Z,
                           0.55f)))
        {
            if (UiFonts.Window(ImGui.GetFontSize(), ImGui.BeginPopup(
                    $"##djSavedStationActions{index}")))
            {
                if (ImGui.MenuItem(
                        "Edit station"))
                {
                    OpenDjStationEditor(
                        index);
                }

                using (ImRaii.PushColor(
                           ImGuiCol.Text,
                           Danger))
                {
                    if (ImGui.MenuItem(
                            "Remove station"))
                    {
                        djSavedStations.RemoveAt(
                            index);

                        Plugin.Cfg.Save();
                    }
                }

                ImGui.EndPopup();
            }
        }

        ImGui.SetCursorScreenPos(
            new Vector2(
                maximum.X -
                menuWidth -
                (showPartyAction
                    ? partyWidth + buttonGap
                    : 0f) -
                playWidth -
                Ui(14f),
                origin.Y + Ui(7f)));

        using (ImRaii.PushColor(
                   ImGuiCol.Button,
                   FrameBgHover)
                   .Push(
                       ImGuiCol.ButtonHovered,
                       Accent)
                   .Push(
                       ImGuiCol.ButtonActive,
                       AccentActive))
        {
            if (ImGui.Button(
                    $"Play##djStation_{index}",
                    new Vector2(
                        playWidth,
                        Ui(30f))))
            {
                PlayDjStation(
                    station);
            }
        }

        if (ImGui.IsItemHovered() &&
            displayedUrl != station.Url)
        {
            UiFonts.SetTooltip(
                station.Url);
        }

        if (showPartyAction)
        {
            ImGui.SetCursorScreenPos(
                new Vector2(
                    maximum.X -
                    menuWidth -
                    partyWidth -
                    Ui(13f),
                    origin.Y +
                    Ui(7f)));

            using (ImRaii.PushColor(
                       ImGuiCol.Button,
                       FrameBgHover)
                   .Push(
                       ImGuiCol.ButtonHovered,
                       Accent)
                   .Push(
                       ImGuiCol.ButtonActive,
                       AccentActive))
            {
                if (ImGui.Button(
                        $"Play in Watch Party##djStationParty_{index}",
                        new Vector2(
                            partyWidth,
                            Ui(30f))))
                {
                    BeginRadioStationWatchParty(
                        station.Url,
                        station.Name);
                }
            }
        }

        ImGui.SetCursorScreenPos(
            new Vector2(
                origin.X,
                maximum.Y));

        ImGui.Dummy(
            new Vector2(
                width,
                1f));
    }


    private void OpenDjStationEditor(
    int? stationIndex)
    {
        djStationGenreDraft.Clear();
        djEditingStationIndex =
            stationIndex;

        djStationEditorError =
            null;

        if (stationIndex is { } index &&
            index >= 0 &&
            index < djSavedStations.Count)
        {
            var station =
                djSavedStations[index];

            djStationGenreDraft.UnionWith(station.Genres ?? []);

            djStationNameInput =
                station.Name;

            djStationUrlInput =
                station.Url;
        }
        else
        {
            djStationNameInput =
                string.Empty;

            djStationUrlInput =
                string.Empty;
        }

        djAddStationPopupOpen =
            true;
    }


    private void PlayDjStation(
        SavedRadioStationRecord station)
    {
        PlayDjUrl(
            station.Url,
            station.Name,
            "Radio");
    }


    // ---------------------------------------------------------
    // Shared audio playback
    // ---------------------------------------------------------

    private void IssueAlphaChannelRadio()
    {
        if (AccountBroadcastRestricted)
        {
            radioError = "Broadcasting requires XIVAuth verification or Patreon";
            return;
        }
        var session = CurrentSession;
        var token = session?.Token;
        var accountId = session?.AccountId;

        if (string.IsNullOrEmpty(
                token))
        {
            radioError =
                "Sign in to get broadcast connection details.";

            return;
        }

        if (djCredentialsLoading)
        {
            return;
        }

        radioError =
            null;

        djCredentialsLoading =
            true;

        _ = Task.Run(
            async () =>
            {
                try
                {
                    var issued =
                        await radioClient
                            .IssueAsync(
                                token)
                            .ConfigureAwait(false);

                    if (CurrentSession?.AccountId != accountId) return;

                    if (issued is null)
                    {
                        radioError =
                            AccountBroadcastRestricted ? "Broadcasting requires XIVAuth verification or Patreon" :
                            "Couldn't get broadcast connection details.";

                        return;
                    }

                    djCredentialsAccountId = accountId;
                    radioCredentials =
                        issued;

                    //
                    // Retain the listening URL internally for connection
                    // checks and Watch Party playback. It is not displayed.
                    //

                    djStreamUrl =
                        issued.ListenUrl;

                    djConnectionSearchActive =
                        false;

                    djConnectionMonitoringEnabled =
                        false;

                    djConnectionCheckInProgress =
                        false;

                    djIsOnAir =
                        false;

                    djOnAirSinceUtc =
                        null;

                    djNextConnectionCheckUtc =
                        DateTime.MaxValue;
                }
                catch (Exception)
                {
                    if (CurrentSession?.AccountId == accountId)
                        radioError = "Could not load your DJ connection settings. Retrying shortly.";
                    AepLog.Warning("[Radio] Could not load DJ credentials.");
                }
                finally
                {
                    djCredentialsLoading =
                        false;
                }
            });
    }

    private void PlayDjStream()
    {
        var url =
            djStreamUrl.Trim();

        if (string.IsNullOrWhiteSpace(
                url))
        {
            return;
        }

        PlayDjUrl(
            url,
            "Live music stream",
            "Music / DJ");

        djStreamUrl =
            string.Empty;
    }


    private void PlayDjUrl(
    string url,
    string title,
    string source)
    {
        if (stream.Mode == StreamMode.Viewing)
        {
            Plugin.ChatGui.Print("[AlphaChannel] Only the host can change playback. Leave the party to play your own radio.");
            return;
        }
        if (string.IsNullOrWhiteSpace(
                url))
        {
            return;
        }

        //
        // Preserve the currently selected Watch Party visualizer and colour
        // whenever a different radio station or audio stream is started.
        //
        // AudioVisualizerSelection removes any existing visualizer fragment
        // before adding the current selection, preventing duplicate fragments.
        //
        var playbackUrl =
            BuildPartyAudioPresentationUrl(url);

        queue.PlayTransient(
            new Video.VideoQueueEntry(
                playbackUrl,
                title,
                source,
                null,
                null,
                0d,
                true));

    }


    private void DrawDjSetupCompactCard(
        string id,
        float width,
        FontAwesomeIcon icon,
        string title,
        string subtitle,
        Action onClick)
    {
        var height = NativeUi(62f);

        var origin =
            ImGui.GetCursorScreenPos();

        var clicked =
            ImGui.InvisibleButton(
                id,
                new Vector2(
                    width,
                    height));

        var hovered =
            ImGui.IsItemHovered();

        if (clicked)
        {
            onClick();
        }


        var drawList =
            ImGui.GetWindowDrawList();

        var min =
            origin;

        var max =
            origin +
            new Vector2(
                width,
                height);


        drawList.AddRectFilled(
            min,
            max,
            ImGui.GetColorU32(
                hovered
                    ? CardBgHover
                    : CardBg),
            9f);

        drawList.AddRect(
            min,
            max,
            ImGui.GetColorU32(
                hovered
                    ? new Vector4(
                        Accent.X,
                        Accent.Y,
                        Accent.Z,
                        0.50f)
                    : BorderSubtle),
            9f,
            ImDrawFlags.None,
            1f);


        //
        // Icon
        //

        using (
            UiFonts.PushIcon())
        {
            var glyph =
                icon.ToIconString();

            var glyphSize =
                ImGui.CalcTextSize(
                    glyph);

            UiFonts.DrawText(drawList, ImGui.GetFontSize(), min +
                new Vector2(
                    Ui(17f),
                    (height -
                     glyphSize.Y) /
                    2f), ImGui.GetColorU32(
                    Accent), glyph);
        }


        //
        // Text
        //

        UiFonts.DrawText(drawList, ImGui.GetFontSize(), min +
            UiVec(49f, 12f), ImGui.GetColorU32(
                Vector4.One), title);

        UiFonts.DrawText(drawList, ImGui.GetFontSize(), min +
            UiVec(49f, 34f), ImGui.GetColorU32(
                MutedText), subtitle);


        //
        // Chevron
        //

        using (
            UiFonts.PushIcon())
        {
            var chevron =
                FontAwesomeIcon.ChevronRight
                    .ToIconString();

            var chevronSize =
                ImGui.CalcTextSize(
                    chevron);

            UiFonts.DrawText(drawList, ImGui.GetFontSize(), max -
                new Vector2(
                    chevronSize.X +
                    Ui(15f),
                    height / 2f +
                    chevronSize.Y /
                    2f), ImGui.GetColorU32(
                    MutedText), chevron);
        }
    }
    private bool pendingWatchPartyCreationOverlayWasOpen;

    private void DrawPendingWatchPartyCreationOverlay()
    {
        if (!watchPartyCreationPopupOpen)
        {
            pendingWatchPartyCreationOverlayWasOpen = false;
            return;
        }

        var parentPosition =
            ImGui.GetWindowPos();

        var parentSize =
            ImGui.GetWindowSize();

        var panelWidth = Ui(680f);
        var panelHeight = Ui(520f);

        var panelPosition =
            parentPosition +
            new Vector2(
                (parentSize.X - panelWidth) * 0.5f,
                (parentSize.Y - panelHeight) * 0.5f);

        ImGui.SetNextWindowPos(
            parentPosition,
            ImGuiCond.Always);

        ImGui.SetNextWindowSize(
            parentSize,
            ImGuiCond.Always);

        ImGui.SetNextWindowBgAlpha(
            0f);

        if (!pendingWatchPartyCreationOverlayWasOpen)
        {
            
        }
        pendingWatchPartyCreationOverlayWasOpen = true;

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
                "##pendingWatchPartyCreationOverlay",
                overlayFlags))
        {
            ImGui.End();
            return;
        }

        var drawList =
            ImGui.GetWindowDrawList();

        drawList.AddRectFilled(
            parentPosition,
            parentPosition + parentSize,
            ImGui.GetColorU32(
                new Vector4(
                    0f,
                    0f,
                    0f,
                    0.54f)));

        ImGui.SetCursorScreenPos(
            panelPosition);

        using (ImRaii.PushStyle(
                   ImGuiStyleVar.ChildRounding,
                   Ui(14f)))
        using (ImRaii.PushStyle(
                   ImGuiStyleVar.ChildBorderSize,
                   Ui(1f)))
        using (ImRaii.PushStyle(
                   ImGuiStyleVar.WindowPadding,
                   UiVec(
                       24f,
                       20f)))
        using (ImRaii.PushColor(
                   ImGuiCol.ChildBg,
                   CardBg))
        using (ImRaii.PushColor(
                   ImGuiCol.Border,
                   new Vector4(
                       Accent.X,
                       Accent.Y,
                       Accent.Z,
                       0.82f)))
        using (var panel =
               UiFonts.Window(ImGui.GetFontSize(), ImRaii.Child(
                   "##pendingWatchPartyCreationPanel",
                   new Vector2(
                       panelWidth,
                       panelHeight),
                   true,
                   ImGuiWindowFlags.NoScrollbar |
                   ImGuiWindowFlags.NoScrollWithMouse)))
        {
            if (panel)
            {
                using var uiFontScale102166=UiFonts.PushRelative(1.18f);

                ImGui.TextColored(
                    Vector4.One,
                    "Create a Watch Party");

                uiFontScale102166.Dispose();

                var creationDescription =
                    pendingWatchPartyMediaKind switch
                    {
                        PendingWatchPartyMediaKind.TwitchRoom =>
                            "Create a Watch Party, then watch your Twitch stream together.",
                        PendingWatchPartyMediaKind.GameRoom =>
                            "Create a room to share your gameplay with friends.",

                        PendingWatchPartyMediaKind.Whiteboard =>
                            "Create a room to share your canvas with friends.",

                        PendingWatchPartyMediaKind.Quiz =>
                            "Create a room before starting your quiz.",

                        PendingWatchPartyMediaKind.Pictionary =>
                            "Create a room before starting Pictionary.",

                        PendingWatchPartyMediaKind.Roulette =>
                            "Create a room before starting roulette.",

                        PendingWatchPartyMediaKind.Bingo =>
                            "Create a room before starting Bingo.",

                        PendingWatchPartyMediaKind.BlastParty => "Create a room before starting Blast Party.",
                        PendingWatchPartyMediaKind.SkyRail =>
                            "Create a room before starting Air Force EX.",

                        PendingWatchPartyMediaKind.LuckyWheel =>
                            "Create a room before starting Lucky Wheel.",

                        PendingWatchPartyMediaKind.SlotMachine =>
                            "Create a room before starting Slot Machine.",

                        PendingWatchPartyMediaKind.Poker =>
                            "Create a room before starting Poker EX.",

                        PendingWatchPartyMediaKind.ChocoRace =>
                            "Create a room before starting Choco RaceBets.",


                        PendingWatchPartyMediaKind.LiveStreamBroadcast =>
                            "Create a room before broadcasting your live video stream.",

                        PendingWatchPartyMediaKind.LocalVideoRoom or
                        PendingWatchPartyMediaKind.LocalVideoStartAndBroadcast or
                        PendingWatchPartyMediaKind.LocalVideoCurrentPlaybackBroadcast =>
                            "Create a room before broadcasting your local video.",

                        PendingWatchPartyMediaKind.WebLink =>
                            "Create a room and play this video for your Watch Party.",

                        PendingWatchPartyMediaKind.RadioStation =>
                            "Create a room and play this radio station for your Watch Party.",

                        _ =>
                            "Create a room before broadcasting your DJ stream."
                    };

                ImGui.TextColored(
                    MutedText,
                    creationDescription);

                ImGui.Dummy(
    UiVec(
        0f,
        14f));

                var closeSize =
                    ImGui.CalcTextSize(
                        FontAwesomeIcon.Times
                            .ToIconString());

                ImGui.SetCursorScreenPos(
                    new Vector2(
                        panelPosition.X +
                        panelWidth -
                        Ui(24f) -
                        closeSize.X,
                        panelPosition.Y +
                        Ui(20f)));

                using (UiFonts.PushIcon())
                {
                    if (ImGui.InvisibleButton(
                            "##closePendingWatchPartyCreation",
                            closeSize))
                    {
                        CancelPendingWatchPartyMedia();
                    }

                    UiFonts.DrawText(ImGui.GetWindowDrawList(), ImGui.GetFontSize(), ImGui.GetItemRectMin(), ImGui.GetColorU32(
                                Vector4.One), FontAwesomeIcon.Times
                                .ToIconString());
                }

                ImGui.SetCursorScreenPos(
                    panelPosition +
                    UiVec(
                        24f,
                        92f));

                using (UiFonts.PushRelative(1f, emphasis: true))
                    ImGui.TextColored(Hex(0xE2E7EF), "Room details");

                ImGui.SetCursorScreenPos(panelPosition + UiVec(24f, 124f));

                DrawCreateRoomFields(
                    panelWidth -
                    Ui(48f));

                var buttonWidth =
                    (panelWidth -
                     Ui(48f) -
                     Ui(10f)) /
                    2f;

                var footerY =
                    panelPosition.Y +
                    panelHeight -
                    Ui(60f);

                ImGui.SetCursorScreenPos(
                    new Vector2(
                        panelPosition.X +
                        Ui(24f),
                        footerY));

                DrawDjActionButton(
                    "##cancelPendingWatchPartyCreation",
                    FontAwesomeIcon.Times,
                    "Cancel",
                    new Vector2(
                        buttonWidth,
                        Ui(38f)),
                    false,
                    CancelPendingWatchPartyMedia);

                ImGui.SameLine(
                    0f,
                    Ui(10f));

                DrawDjActionButton(
                    "##createPendingWatchParty",
                    FontAwesomeIcon.Users,
                    "Create Watch Party",
                    new Vector2(
                        buttonWidth,
                        Ui(38f)),
                    false,
                    CreateEmptyWatchParty,
                    true);
            }
        }

        ImGui.End();
    }

    // ---------------------------------------------------------
    // Add/edit saved radio station overlay
    // ---------------------------------------------------------

    private void DrawDjStationEditorOverlay()
    {
        if (!djAddStationPopupOpen) return;
        djAddStationPopupOpen = false;
        OpenMusicDialog("station");
    }

    private void SaveDjStationEditor()
    {
        var name =
            djStationNameInput.Trim();

        var url =
            djStationUrlInput.Trim();

        if (string.IsNullOrWhiteSpace(
                name))
        {
            djStationEditorError =
                "Enter a station name.";

            return;
        }

        if (!Uri.TryCreate(
                url,
                UriKind.Absolute,
                out var parsedUrl) ||
            (parsedUrl.Scheme != Uri.UriSchemeHttp &&
             parsedUrl.Scheme != Uri.UriSchemeHttps))
        {
            djStationEditorError =
                "Enter a valid HTTP or HTTPS direct stream URL.";

            return;
        }

        if (djEditingStationIndex is { } index &&
            index >= 0 &&
            index < djSavedStations.Count)
        {
            djSavedStations[index].Name =
                name;

            djSavedStations[index].Url =
                url;

            djSavedStations[index].Genres = RadioGenres.Where(djStationGenreDraft.Contains).ToList();
        }
        else
        {
            djSavedStations.Add(
                new SavedRadioStationRecord
                {
                    Name =
                        name,

                    Url =
                        url,
                    Genres = RadioGenres.Where(djStationGenreDraft.Contains).ToList(),
                });
        }

        Plugin.Cfg.Save();

        CloseDjStationEditor();
    }

    private void CloseDjStationEditor()
    {
        djAddStationPopupOpen =
            false;

        djEditingStationIndex =
            null;

        djStationEditorError =
            null;

        djStationNameInput =
            string.Empty;

        djStationUrlInput =
            string.Empty;
    }

    // ---------------------------------------------------------
    // Unified Icecast connection guide
    // ---------------------------------------------------------

    private void DrawDjConnectionGuideOverlay()
    {
        if (!djConnectionGuideOpen) return;
        djConnectionGuideOpen = false;
        OpenMusicDialog("guide");
    }

    private void DrawDjConnectionGuideTabs()
    {
        string[] tabs =
        [
            "BUTT",
        "Mixxx",
        "AltaCast",
        "Rocket",
        "Other",
    ];

        var gap = NativeUi(8f);

        var availableWidth =
            ImGui.GetContentRegionAvail().X;

        var tabWidth =
            (availableWidth -
             gap * (tabs.Length - 1)) /
            tabs.Length;

        for (var index = 0;
             index < tabs.Length;
             index++)
        {
            if (index > 0)
            {
                ImGui.SameLine(
                    0f,
                    gap);
            }

            var selected =
                djConnectionGuideTab ==
                index;

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
                           AccentActive))
            {
                if (ImGui.Button(
                        $"{tabs[index]}##djConnectionGuideTab{index}",
                        new Vector2(
                            tabWidth,
                            Ui(36f))))
                {
                    djConnectionGuideTab =
                        index;
                }
            }
        }
    }

    private void DrawDjConnectionGuideTabContent()
    {
        switch (djConnectionGuideTab)
        {
            case 0:
                DrawDjButtGuide();
                break;

            case 1:
                DrawDjMixxxGuide();
                break;

            case 2:
                DrawDjAltaCastGuide();
                break;

            case 3:
                DrawDjRocketGuide();
                break;

            default:
                DrawDjGenericIcecastGuide();
                break;
        }
    }

    private void DrawDjButtGuide()
    {
        DrawDjSoftwareGuideHeading(
            "BUTT",
            "Broadcast Using This Tool is a lightweight application designed specifically for Icecast and Shoutcast broadcasting.");

        DrawDjGuideNumberedLine(
            "1",
            "Open BUTT and select Settings.");

        DrawDjGuideNumberedLine(
            "2",
            "In the Main tab, add a new server.");

        DrawDjGuideNumberedLine(
            "3",
            "Set the server type to IceCast and copy the AlphaChannel connection values into Address, Port, User, Password and Mount.");

        DrawDjGuideNumberedLine(
            "4",
            "Open the Audio tab and select MP3 or OGG, stereo, with a 128 kbps target where available.");

        DrawDjGuideNumberedLine(
            "5",
            "Save the settings, choose your audio input, then press the play button in BUTT to connect.");

        DrawDjReturnAndCheckNotice();
    }

    private void DrawDjMixxxGuide()
    {
        DrawDjSoftwareGuideHeading(
            "Mixxx",
            "Mixxx provides decks, playlists, mixing controls and native Icecast live broadcasting.");

        DrawDjGuideNumberedLine(
            "1",
            "Open Preferences and select Live Broadcasting.");

        DrawDjGuideNumberedLine(
            "2",
            "Set Type to Icecast 2.");

        DrawDjGuideNumberedLine(
            "3",
            "Enter AlphaChannel's Server, Port, Mount, Username and Password in the matching fields.");

        DrawDjGuideNumberedLine(
            "4",
            "Select MP3 or OGG, stereo, and use a 128 kbps target where available.");

        DrawDjGuideNumberedLine(
            "5",
            "Apply the settings, then enable live broadcasting from Mixxx's Options menu.");

        DrawDjReturnAndCheckNotice();
    }

    private void DrawDjAltaCastGuide()
    {
        DrawDjSoftwareGuideHeading(
            "AltaCast",
            "AltaCast is a traditional encoder for sending an existing audio mix to an Icecast server.");

        DrawDjGuideNumberedLine(
            "1",
            "Open AltaCast and add a new encoder.");

        DrawDjGuideNumberedLine(
            "2",
            "Open the encoder configuration and select Icecast2.");

        DrawDjGuideNumberedLine(
            "3",
            "Enter the AlphaChannel Server, Port, Username, Password and Mountpoint.");

        DrawDjGuideNumberedLine(
            "4",
            "Choose MP3 or OGG and configure stereo audio at an appropriate bitrate.");

        DrawDjGuideNumberedLine(
            "5",
            "Select the audio source carrying your DJ mix, then connect the encoder.");

        DrawDjReturnAndCheckNotice();
    }

    private void DrawDjRocketGuide()
    {
        DrawDjSoftwareGuideHeading(
            "Rocket Broadcaster",
            "Rocket Broadcaster combines microphone, music and application audio for live radio-style production.");

        DrawDjGuideNumberedLine(
            "1",
            "Open Broadcast Streams and add a new stream.");

        DrawDjGuideNumberedLine(
            "2",
            "Choose Icecast 2 as the streaming server type.");

        DrawDjGuideNumberedLine(
            "3",
            "Enter AlphaChannel's Server, Port, Mount, Username and Password.");

        DrawDjGuideNumberedLine(
            "4",
            "Select a supported MP3 or OGG encoder and configure your desired audio sources.");

        DrawDjGuideNumberedLine(
            "5",
            "Start broadcasting and confirm that Rocket reports a successful connection.");

        DrawDjReturnAndCheckNotice();
    }

    private void DrawDjGenericIcecastGuide()
    {
        DrawDjSoftwareGuideHeading(
            "Other Icecast software",
            "Most Icecast-compatible applications request the same basic connection values.");

        DrawDjGuideSetting(
            "Address / Host",
            "AlphaChannel Server");

        DrawDjGuideSetting(
            "Port",
            "AlphaChannel Port");

        DrawDjGuideSetting(
            "User / Login",
            "AlphaChannel Username");

        DrawDjGuideSetting(
            "Password",
            "AlphaChannel Password");

        DrawDjGuideSetting(
            "Mount / Mountpoint",
            "AlphaChannel Mount");

        ImGui.Dummy(
            UiVec(0f, 12f));

        ImGui.TextWrapped(
            "Select Icecast or Icecast 2 when the software asks for a server type. Use MP3 or OGG, stereo, and a 128 kbps target where supported.");

        DrawDjReturnAndCheckNotice();
    }

    private void DrawDjSoftwareGuideHeading(
        string title,
        string description)
    {
        using var uiFontScale125003=UiFonts.PushRelative(1.12f);

        ImGui.TextColored(
            Hex(0xE2E7EF),
            title);

        uiFontScale125003.Dispose();

        using (UiFonts.PushRelative(0.90f))
        using (ImRaii.PushColor(ImGuiCol.Text, MutedText))
            ImGui.TextWrapped(description);

        ImGui.Dummy(
            UiVec(0f, 14f));
    }

    private static void DrawDjReturnAndCheckNotice()
    {
        ImGui.Dummy(
            UiVec(0f, 12f));

        using (ImRaii.PushStyle(
                   ImGuiStyleVar.ChildRounding,
                   8f)
                   .Push(
                       ImGuiStyleVar.WindowPadding,
                       UiVec(12f, 10f)))
        using (ImRaii.PushColor(
                   ImGuiCol.ChildBg,
                   new Vector4(
                       Accent.X * 0.10f,
                       Accent.Y * 0.10f,
                       Accent.Z * 0.16f,
                       0.95f))
                   .Push(
                       ImGuiCol.Border,
                       new Vector4(
                           Accent.X,
                           Accent.Y,
                           Accent.Z,
                           0.34f)))
        {
            if (UiFonts.Window(ImGui.GetFontSize(), ImGui.BeginChild(
                    "##djReturnAndCheck",
                    UiVec(-1f, 58f),
                    true,
                    ImGuiWindowFlags.NoScrollbar |
                    ImGuiWindowFlags.NoScrollWithMouse)))
            {
                ImGui.TextColored(
                    AccentHover,
                    "Return to Alpha Channel");

                ImGui.TextColored(
                    MutedText,
                    "Wait for ON AIR, or press Check connection after your software reports that it is connected.");
            }

            ImGui.EndChild();
        }
    }

    private void DrawDjSimpleStep1()
    {
        DrawDjGuideHeading(
            "1. Create your streaming account",
            "Caster.fm can provide the online audio stream that Alpha Channel listens to.");

        ImGui.TextWrapped(
            "Create a Caster.fm account and set up a radio stream.");

        ImGui.Dummy(
            UiVec(0f, 12f));

        ImGui.TextWrapped(
            "Caster.fm is our recommended simple option, but Alpha Channel itself only needs a compatible direct audio stream URL.");

        ImGui.Dummy(
            UiVec(0f, 16f));

        DrawDjExternalButton(
            "Open Caster.fm",
            "https://www.caster.fm/");
    }

    private void DrawDjSimpleStep2()
    {
        DrawDjGuideHeading(
            "2. Install the broadcaster",
            "Use Caster.fm Broadcaster to send your music and microphone audio to your stream.");

        ImGui.TextWrapped(
            "Install Caster.fm Broadcaster and sign in or enter the connection details provided by your Caster.fm stream.");

        ImGui.Dummy(
            UiVec(0f, 16f));

        DrawDjExternalButton(
            "Open Broadcaster Page",
            "https://www.caster.fm/free-cloud-stream-hosting/broadcaster-software/");
    }

    private void DrawDjSimpleStep3()
    {
        DrawDjGuideHeading(
            "3. Choose your audio",
            "Select what you want your Alpha Channel watch party to hear.");

        DrawDjGuideBullet(
            "Music",
            "Choose the audio source carrying your music.");

        DrawDjGuideBullet(
            "Microphone",
            "Enable your microphone if you want to talk to listeners.");

        DrawDjGuideBullet(
            "Levels",
            "Watch the audio meters and make sure they move while sound is playing.");

        ImGui.Dummy(
            UiVec(0f, 10f));

        ImGui.TextWrapped(
            "Keep the levels below clipping. If the stream sounds too quiet or distorted, adjust the source levels in the broadcaster before changing Alpha Channel's playback volume.");
    }

    private void DrawDjSimpleStep4()
    {
        DrawDjGuideHeading(
            "4. Start broadcasting",
            "Once your stream is online, give its direct audio URL to Alpha Channel.");

        DrawDjGuideNumberedLine(
            "1",
            "Start broadcasting from Caster.fm Broadcaster.");

        DrawDjGuideNumberedLine(
            "2",
            "Find the direct listening or stream URL provided for your station.");

        DrawDjGuideNumberedLine(
            "3",
            "Copy that URL.");

        DrawDjGuideNumberedLine(
            "4",
            "Close this guide and paste it into the Music / DJ box.");

        DrawDjGuideNumberedLine(
            "5",
            "Press Play on TV to start it for your watch party.");
    }

    private void DrawDjAdvancedStep1()
    {
        DrawDjGuideHeading(
            "1. Choose your stream host",
            "Your stream host provides the public audio URL that Alpha Channel plays.");

        ImGui.TextWrapped(
            "We recommend Caster.fm as an easy starting point.");

        ImGui.Dummy(
            UiVec(0f, 10f));

        ImGui.TextWrapped(
            "You do not have to use Caster.fm. Other Icecast, Shoutcast or internet radio services can work as long as they provide a compatible direct audio stream URL.");

        ImGui.Dummy(
            UiVec(0f, 16f));

        DrawDjExternalButton(
            "Open Caster.fm",
            "https://www.caster.fm/");
    }

    private void DrawDjAdvancedStep2()
    {
        DrawDjGuideHeading(
            "2. Install your DJ software",
            "Mixxx is our recommended free option for live DJ sets.");

        ImGui.TextWrapped(
            "Mixxx gives you decks, playlists, mixing controls and live broadcasting support.");

        ImGui.Dummy(
            UiVec(0f, 12f));

        ImGui.TextWrapped(
            "Other broadcasting software can also be used. BUTT is a lightweight option if your audio is already being mixed elsewhere.");

        ImGui.Dummy(
            UiVec(0f, 16f));

        DrawDjExternalButton(
            "Open Mixxx Website",
            "https://mixxx.org/");
    }

    private void DrawDjAdvancedStep3()
    {
        DrawDjGuideHeading(
            "3. Connect your DJ software",
            "Enter the broadcasting details supplied by your stream host.");

        DrawDjGuideBullet(
            "Server / Host",
            "The address supplied by your radio host.");

        DrawDjGuideBullet(
            "Port",
            "The broadcasting port supplied by your host.");

        DrawDjGuideBullet(
            "Mount",
            "Your Icecast mount point, when required.");

        DrawDjGuideBullet(
            "Username / Password",
            "The source credentials supplied by your host.");

        ImGui.Dummy(
            UiVec(0f, 10f));

        ImGui.TextWrapped(
            "In Mixxx, these settings are configured in the live broadcasting section.");
    }

    private void DrawDjAdvancedStep4()
    {
        DrawDjGuideHeading(
            "4. Configure your audio",
            "Use a broadly compatible stream format and sensible bitrate.");

        DrawDjGuideSetting(
            "Format",
            "MP3");

        DrawDjGuideSetting(
            "Bitrate",
            "128 - 160 Kbps");

        DrawDjGuideSetting(
            "Channels",
            "Stereo");

        ImGui.Dummy(
            UiVec(0f, 12f));

        ImGui.TextWrapped(
            "Your hosting provider may impose its own bitrate limit. If so, use the highest compatible setting allowed by that service.");

        ImGui.Dummy(
            UiVec(0f, 10f));

        ImGui.TextWrapped(
            "Before going live, check that your music and microphone levels are balanced and are not clipping.");
    }

    private void DrawDjAdvancedStep5()
    {
        DrawDjGuideHeading(
            "5. Broadcast to Alpha Channel",
            "Start your DJ broadcast, then give Alpha Channel the direct listening URL.");

        DrawDjGuideNumberedLine(
            "1",
            "Start live broadcasting in Mixxx or your chosen broadcasting software.");

        DrawDjGuideNumberedLine(
            "2",
            "Confirm your radio host shows the stream as online.");

        DrawDjGuideNumberedLine(
            "3",
            "Copy the direct stream or listening URL from your radio host.");

        DrawDjGuideNumberedLine(
            "4",
            "Close this guide and paste the URL into Music / DJ.");

        DrawDjGuideNumberedLine(
            "5",
            "Press Play on TV.");

        ImGui.Dummy(
            UiVec(0f, 10f));

        ImGui.TextColored(
            Gold,
            "Important");

        ImGui.TextWrapped(
            "A station webpage is not necessarily the audio stream itself. Alpha Channel needs the direct stream URL that an audio player can open.");
    }

    // ---------------------------------------------------------
    // Shared guide shell
    // ---------------------------------------------------------

    private void DrawDjGuideShell(
        string title,
        string subtitle,
        int stepCount,
        ref int currentStep,
        Action<int> drawStep,
        Action? onDone = null,
        Vector4? textColor = null)
    {
        using var uiFontScale138906=UiFonts.PushRelative(1.15f);

        ImGui.TextColored(
            textColor ?? Vector4.One,
            title);

        uiFontScale138906.Dispose();

        ImGui.Dummy(
            UiVec(0f, 3f));

        using var uiFontScale139129=UiFonts.PushRelative(0.82f);

        ImGui.TextColored(
            MutedText,
            subtitle);

        uiFontScale139129.Dispose();

        ImGui.Dummy(
            UiVec(0f, 14f));

        var availableWidth =
            ImGui.GetContentRegionAvail().X;

        var spacing = NativeUi(8f);

        var stepWidth =
            (availableWidth -
             spacing * (stepCount - 1)) /
            stepCount;

        for (var i = 0;
             i < stepCount;
             i++)
        {
            if (i > 0)
            {
                ImGui.SameLine(
                    0f,
                    spacing);
            }

            var selected =
                currentStep == i;

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
                    AccentActive))
            {
                if (ImGui.Button(
                    $"{i + 1}##djGuideStep_{title}_{i}",
                    new Vector2(
                        stepWidth,
                        Ui(36f))))
                {
                    currentStep = i;
                }
            }
        }

        ImGui.Dummy(
            UiVec(0f, 14f));

        ImGui.Separator();

        ImGui.Dummy(
            UiVec(0f, 8f));

        var footerHeight = Ui(58f);

        // Only the actual guide content scrolls.
        // Navigation stays fixed at the bottom.
        using (ImRaii.PushStyle(
            ImGuiStyleVar.WindowPadding,
            UiVec(16f, 14f)))
        using (var content =
            UiFonts.Window(ImGui.GetFontSize(), ImRaii.Child(
                $"##djGuideContent_{title}",
                new Vector2(
                    -1f,
                    -footerHeight),
                false,
                ImGuiWindowFlags.None)))
        {
            if (content)
            {
                using var guideText = ImRaii.PushColor(ImGuiCol.Text, textColor ?? Vector4.One);
                drawStep(
                    currentStep);

                ImGui.Dummy(
                    UiVec(0f, 6f));
            }
        }

        ImGui.Separator();

        ImGui.Dummy(
            UiVec(0f, 8f));

        if (currentStep > 0)
        {
            if (ImGui.Button(
                $"Back##{title}",
                UiVec(90f, 32f)))
            {
                currentStep--;
            }
        }
        else
        {
            ImGui.Dummy(
                UiVec(90f, 32f));
        }

        ImGui.SameLine();

        var rightButtonWidth = Ui(100f);

        ImGui.SetCursorPosX(
            ImGui.GetWindowWidth() -
            rightButtonWidth -
            16f);

        using (ImRaii.PushStyle(
            ImGuiStyleVar.FrameRounding,
            7f))
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
            if (currentStep <
                stepCount - 1)
            {
                if (ImGui.Button(
                    $"Next##{title}",
                    new Vector2(
                        rightButtonWidth,
                        Ui(32f))))
                {
                    currentStep++;
                }
            }
            else
            {
                if (ImGui.Button(
       $"Done##{title}",
       new Vector2(
           rightButtonWidth,
           Ui(32f))))
                {
                    if (onDone is not null)
                    {
                        onDone();
                    }
                    else
                    {
                        ImGui.CloseCurrentPopup();
                    }
                }
            }
        }
    }

    // ---------------------------------------------------------
    // Shared guide helpers
    // ---------------------------------------------------------

    private void DrawDjGuideHeading(
        string title,
        string subtitle)
    {
        using var uiFontScale143679=UiFonts.PushRelative(1.08f);

        ImGui.TextColored(
            Vector4.One,
            title);

        uiFontScale143679.Dispose();

        ImGui.Dummy(
            UiVec(0f, 4f));

        using var uiFontScale143902=UiFonts.PushRelative(0.82f);

        ImGui.TextColored(
            MutedText,
            subtitle);

        uiFontScale143902.Dispose();

        ImGui.Dummy(
            UiVec(0f, 16f));
    }

    private void DrawDjGuideBullet(
        string title,
        string description)
    {
        ImGui.TextColored(
            Accent,
            "•");

        ImGui.SameLine();

        ImGui.TextColored(
            Vector4.One,
            title);

        ImGui.SameLine();

        ImGui.TextColored(
            MutedText,
            $"— {description}");

        ImGui.Dummy(
            UiVec(0f, 6f));
    }

    private void DrawDjGuideSetting(
        string name,
        string value)
    {
        var startX =
            ImGui.GetCursorPosX();

        ImGui.TextColored(
            MutedText,
            name);

        ImGui.SameLine();

        ImGui.SetCursorPosX(
            startX + 180f);

        ImGui.TextColored(
            Hex(0xE2E7EF),
            value);

        ImGui.Dummy(
            UiVec(0f, 6f));
    }

    private void DrawDjGuideNumberedLine(
        string number,
        string text)
    {
        ImGui.TextColored(
            Accent,
            number + ".");

        ImGui.SameLine(
            0f,
            8f);

        ImGui.TextWrapped(
            text);

        ImGui.Dummy(
            UiVec(0f, 8f));
    }

    private void DrawDjExternalButton(
        string label,
        string url)
    {
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
            var buttonPos =
                ImGui.GetCursorScreenPos();

            var buttonSize =
                UiVec(190f, 36f);

            if (ImGui.Button(
                $"##djExternal_{label}",
                buttonSize))
            {
                Dalamud.Utility.Util.OpenLink(
                    url);
            }

            DrawPlayerActionButtonContent(
                buttonPos,
                buttonSize,
                FontAwesomeIcon.ExternalLinkAlt,
                label,
                Vector4.One);
        }
    }
}

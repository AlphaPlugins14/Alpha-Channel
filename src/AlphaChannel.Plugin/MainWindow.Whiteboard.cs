using System.Globalization;
using AlphaChannel.Plugin.Video;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;

namespace AlphaChannel.Plugin;

// Experimental watch-party whiteboard. Protocol, state, editor and TV rasterisation are kept
// together so the feature can be removed without untangling it from the rest of the UI.
internal sealed partial class MainWindow
{
    private const string WhiteboardUrlPrefix = "alphachannel://whiteboard/";
    private const int WhiteboardPointBatchSize = 14;
    private static readonly TimeSpan WhiteboardBatchInterval = TimeSpan.FromMilliseconds(70);
    private bool CloudflareDrawing => DrawingRelayGame is { } game && GameRelay(game) is not null;
    private int DrawingBatchSize => CloudflareDrawing ? 64 : WhiteboardPointBatchSize;
    private TimeSpan DrawingBatchInterval => CloudflareDrawing ? TimeSpan.FromMilliseconds(100) : WhiteboardBatchInterval;

    private readonly record struct WhiteboardColour(string Name, Vector4 Value, uint Rgb);

    private enum WhiteboardMode
    {
        Solo,
        Team,
    }

    private static readonly WhiteboardColour[] WhiteboardColours =
    [
        new("Black",   new(0.025f, 0.025f, 0.035f, 1f), 0x060609),
        new("White",   new(0.96f, 0.97f, 0.99f, 1f),    0xF5F7FC),
        new("Grey",    new(0.63f, 0.66f, 0.72f, 1f),    0xA1A8B8),
        new("Red",     new(1.00f, 0.24f, 0.27f, 1f),    0xFF3D45),
        new("Coral",   new(1.00f, 0.40f, 0.38f, 1f),    0xFF6661),
        new("Orange",  new(1.00f, 0.59f, 0.19f, 1f),    0xFF9630),
        new("Yellow",  new(1.00f, 0.85f, 0.16f, 1f),    0xFFD929),
        new("Lime",    new(0.67f, 0.94f, 0.15f, 1f),    0xABEF26),
        new("Green",   new(0.11f, 0.76f, 0.40f, 1f),    0x1DC266),
        new("Teal",    new(0.08f, 0.74f, 0.68f, 1f),    0x14BDAE),
        new("Cyan",    new(0.10f, 0.83f, 0.92f, 1f),    0x1AD4EB),
        new("Blue",    new(0.20f, 0.45f, 0.96f, 1f),    0x3373F5),
        new("Purple",  new(0.55f, 0.31f, 0.95f, 1f),    0x8C4FF2),
        new("Magenta", new(0.88f, 0.25f, 0.88f, 1f),    0xE03FE0),
        new("Pink",    new(1.00f, 0.43f, 0.69f, 1f),    0xFF6EB0),
        new("Brown",   new(0.57f, 0.31f, 0.12f, 1f),    0x914F1F),
    ];

    private static readonly float[] WhiteboardBrushSizes = [3f, 6f, 12f, 20f];

    private sealed class WhiteboardStroke
    {
        internal required string Id { get; init; }
        internal int ColourIndex { get; init; }
        internal float Width { get; init; }
        internal int LastSequence { get; set; } = -1;
        internal bool Complete { get; set; }
        internal int Started { get; init; }
        internal List<Vector2> Points { get; } = [];
    }

    private readonly List<WhiteboardStroke> whiteboardStrokes = [];
    private readonly Dictionary<string, WhiteboardStroke> whiteboardStrokeById =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, (DateTime WindowStart, int Count)> whiteboardContributionRates =
        new(StringComparer.Ordinal);
    private readonly List<Vector2> whiteboardPendingPoints = [];
    private string? whiteboardSessionId;
    private string? whiteboardDrawingStrokeId;
    private int whiteboardDrawingSequence;
    private bool whiteboardActive;
    private bool whiteboardRequiresRelay;
    private bool DrawingTransportReady => !whiteboardRequiresRelay || stream.Mode != StreamMode.Viewing || GameRelay("canvas") is not null;
    private bool StandaloneWhiteboardActive => ActiveActivity == ActiveActivityKind.Whiteboard;
    private bool whiteboardBackgroundWhite;
    private int whiteboardColourIndex = 10;
    private float whiteboardBrushWidth = 6f;
    private bool whiteboardTvDirty;
    private DateTime whiteboardLastBatchUtc = DateTime.MinValue;
    private string whiteboardRosterSignature = string.Empty;
    private WhiteboardMode whiteboardMode = WhiteboardMode.Solo;
    private WhiteboardMode pendingWhiteboardMode = WhiteboardMode.Solo;

    internal string? ActiveWhiteboardUrl =>
        whiteboardActive && stream.Mode == StreamMode.Hosting && whiteboardSessionId is { Length: > 0 }
            ? WhiteboardUrlPrefix + whiteboardSessionId + ActivityCanvasSuffix(ActiveActivity) + (BlastPartyActive ? BlastCanvasKeySuffix : "")
            : null;

    internal bool IsWhiteboardUrl(string? url) =>
        url?.StartsWith(WhiteboardUrlPrefix, StringComparison.OrdinalIgnoreCase) == true;

    internal void ActivateWhiteboardFromRemoteState(string url)
    {
        if (!IsWhiteboardUrl(url))
        {
            return;
        }

        var sessionId = url[WhiteboardUrlPrefix.Length..].Split('?', 2)[0];
        if (!string.IsNullOrWhiteSpace(sessionId) &&
            !string.Equals(sessionId, whiteboardSessionId, StringComparison.Ordinal))
        {
            ResetWhiteboard(sessionId, whiteboardBackgroundWhite);
        }

        whiteboardActive = true;
        whiteboardTvDirty = true;
        RenderWhiteboardTvIfNeeded();
    }

    internal void UpdateWhiteboard()
    {
        while (stream.IncomingWhiteboardCommands.TryDequeue(out var incoming))
        {
            if (incoming.Command.StartsWith("V|", StringComparison.Ordinal)) { ReceiveArcadeServerCommand("canvas", incoming.UserId, incoming.Command); continue; }
            if (DrawingRelayGame is { } drawing && GameRelay(drawing) is not null) continue;
            if (stream.Mode == StreamMode.Hosting)
            {
                TryRelayWhiteboardContribution(incoming.UserId, incoming.Command);
            }
            else
            {
                ApplyWhiteboardCommand(incoming.Command);
            }
        }
        UpdateActivityRelay("canvas");

        if (stream.Mode == StreamMode.None)
        {
            // Solo Canvas can run locally without a Watch Party.
            if (whiteboardActive && !screenController.Engine.IsPlayingWhiteboard)
                HandleWhiteboardClosed();
            if (!whiteboardActive)
                whiteboardSessionId = null;
            whiteboardRosterSignature = string.Empty;
            if (!whiteboardActive)
                ResetCanvasActivityPointsClock();
            else
            {
                UpdateCanvasActivityPoints();
                RenderWhiteboardTvIfNeeded();
            }
            return;
        }

        if (stream.Mode == StreamMode.Hosting && whiteboardActive)
        {
            // Selecting another media type gives TV ownership to that renderer and ends this
            // whiteboard session naturally.
            if (!screenController.Engine.IsPlayingWhiteboard)
            {
                HandleWhiteboardClosed();
                return;
            }

            var rosterSignature = string.Join(
                ';',
                stream.Roster.Select(member => member.UserId).Order(StringComparer.Ordinal));
            if (!string.Equals(rosterSignature, whiteboardRosterSignature, StringComparison.Ordinal))
            {
                var previousMembers = whiteboardRosterSignature.Split(';', StringSplitOptions.RemoveEmptyEntries)
                    .ToHashSet(StringComparer.Ordinal);
                var hasNewMember = stream.Roster.Any(member => !previousMembers.Contains(member.UserId));
                whiteboardRosterSignature = rosterSignature;
                if (hasNewMember)
                {
                    // Canvas is live-only in both modes. Activation preserves existing
                    // viewers' strokes and starts newcomers with an empty drawing.
                    // Pictionary still needs its current picture for mid-round spectators.
                    if (DrawingRelayGame is { } drawing && GameRelay(drawing) is not null)
                    {
                        PublishDrawingState();
                    }
                    else if (PictionaryActive || PictionaryTeamsActive)
                    {
                        SendWhiteboardSnapshot();
                    }
                    else
                    {
                        _ = stream.SendWhiteboardCommandAsync(BuildWhiteboardActivationCommand());
                    }
                }
                if (QuizActive) SendQuizState();
                if (rouletteSessionId is not null) SendRouletteState();
                if (BingoActive) SendBingoState();
                if (SkyRailActive) SendSkyRailState();
                if (SlotMachineActive) SendSlotMachineState();
                if (LuckyWheelActive) SendLuckyWheelState();
                if (PokerActive) SendPokerState();
                if (ChocoRaceActive) SendChocoRaceState();
            }
        }

        UpdateCanvasActivityPoints();
        RenderWhiteboardTvIfNeeded();
    }

    private void DrawWhiteboardPage()
    {
        lastActivitiesPage = HomePage.Whiteboard;
        var isHost = stream.Mode == StreamMode.Hosting;
        var isViewer = stream.Mode == StreamMode.Viewing;

        if (ChocoRaceActive)
        {
            ImGui.TextColored(MutedText,
                "Choco RaceBets is using the TV. Its race controls are on the Choco RaceBets page.");
            if (ImGui.Button("Open Choco RaceBets", UiVec(190f, 38f)))
                currentPage = HomePage.RacePreview;
            return;
        }

        using (ImRaii.PushStyle(ImGuiStyleVar.ItemSpacing, UiVec(8f, 8f)))
        {
            if (!StandaloneWhiteboardActive)
            {
                DrawWhiteboardStartPage(isHost, isViewer);
                return;
            }

            DrawWhiteboardToolbar(isHost, isViewer);

            var canDraw = isHost || stream.Mode == StreamMode.None ||
                          (isViewer && whiteboardMode == WhiteboardMode.Team && DrawingTransportReady);
            DrawWhiteboardCanvas(canDraw);

            ImGui.TextColored(
                MutedText,
                whiteboardMode == WhiteboardMode.Team
                    ? isHost
                        ? "Team mode is live. Everyone can draw directly on the TV or from this page; you control the session."
                        : "Draw directly on the TV or from this page. Your strokes appear on every connected Watch Party TV."
                    : stream.Mode == StreamMode.None
                        ? "Draw directly on your TV or from this page. Start a Watch Party to share it with viewers."
                        : isHost
                        ? "Draw directly on the TV or from this page. Changes appear live on every Watch Party TV."
                        : "Solo mode is live. Only the Watch Party host can draw.");
            DrawArcadeSetupParagraph("Solo and Team Canvas are live-only. Late joiners start with a blank canvas and see only drawing sent after they join.");
        }
    }

    private void DrawWhiteboardStartPage(bool isHost, bool isViewer)
    {
        DrawWhiteboardWelcomeHero();
        ImGui.Dummy(UiVec(0f, 6f));

        var available = ImGui.GetContentRegionAvail().X;
        var gap = Ui(12f);
        var cardWidth = Math.Max(Ui(300f), (available - gap) * 0.5f);
        DrawWhiteboardModeCard(
            "solo",
            cardWidth,
            "Recommended",
            "Solo canvas",
            "Draw on your own TV, or share it with viewers when you're hosting a Watch Party.",
            ["Only you can draw", "You control undo and clear", "Late joiners start with a blank canvas"],
            "Start Solo Canvas",
            WhiteboardMode.Solo,
            isHost,
            isViewer);
        ImGui.SameLine(0f, gap);
        DrawWhiteboardModeCard(
            "team",
            cardWidth,
            "Collaborative",
            "Team canvas",
            "Draw together with everyone currently in the Watch Party. The host remains in control.",
            ["Everyone can draw", "Host controls the session", "Late joiners start with a blank canvas"],
            "Start Team Canvas",
            WhiteboardMode.Team,
            isHost,
            isViewer);

        ImGui.Dummy(UiVec(0f, 6f));
        DrawWhiteboardHowItWorks();
        ImGui.Dummy(UiVec(0f, 4f));
        DrawWhiteboardFooterNote();
        DrawArcadeSetupParagraph("Solo and Team Canvas are live-only. Late joiners start with a blank canvas and see only drawing sent after they join.");
    }

    private void DrawWhiteboardWelcomeHero()
    {
        var height = Ui(166f);
        var width = ImGui.GetContentRegionAvail().X;
        using var colours = ImRaii.PushColor(ImGuiCol.ChildBg, CardBg).Push(ImGuiCol.Border, BorderSubtle);
        using var style = ImRaii.PushStyle(ImGuiStyleVar.ChildRounding, Ui(10f))
            .Push(ImGuiStyleVar.ChildBorderSize, Ui(1f))
            .Push(ImGuiStyleVar.WindowPadding, UiVec(20f, 18f));
        if (UiFonts.Window(ImGui.GetFontSize(), ImGui.BeginChild("##whiteboardWelcome", new Vector2(width, height), true, ImGuiWindowFlags.NoScrollbar)))
        {
            var origin = ImGui.GetCursorScreenPos();
            ImGui.SetCursorPosY(ImGui.GetCursorPosY() + Ui(12f));
            using var uiFontScale13264=UiFonts.PushRelative(1.72f);
            ImGui.TextColored(Hex(0xE2E7EF), "Draw on the Watch Party TV");
            uiFontScale13264.Dispose();
            ImGui.Dummy(UiVec(0f, 8f));
            using var uiFontScale13449=UiFonts.PushRelative(.90f);
            ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + width * 0.54f);
            ImGui.TextColored(
                MutedText,
                "Create sketches, reactions, notes, or games in the plugin window. Your drawing appears live on every connected TV.");
            ImGui.PopTextWrapPos();
            uiFontScale13449.Dispose();

            var artMin = origin + new Vector2(width * 0.66f, Ui(2f));
            var artMax = origin + new Vector2(width - Ui(75f), Ui(126f));
            var drawList = ImGui.GetWindowDrawList();
            drawList.AddRectFilled(artMin, artMax, ImGui.GetColorU32(new Vector4(0.02f, 0.025f, 0.04f, 1f)), Ui(9f));
            drawList.AddRect(artMin, artMax, ImGui.GetColorU32(BorderSubtle), Ui(9f));
            var cyan = ImGui.GetColorU32(WhiteboardColours[10].Value);
            var pink = ImGui.GetColorU32(WhiteboardColours[14].Value);
            var yellow = ImGui.GetColorU32(WhiteboardColours[6].Value);
            var artSize = artMax - artMin;
            Vector2 ArtPoint(float x, float y) => artMin + new Vector2(artSize.X * x, artSize.Y * y);
            var strokeWidth = Math.Clamp(artSize.X * 0.018f, Ui(3f), Ui(5f));
            drawList.PushClipRect(artMin, artMax, true);
            drawList.AddBezierCubic(ArtPoint(0.07f, 0.34f), ArtPoint(0.19f, 0.03f), ArtPoint(0.31f, 0.60f), ArtPoint(0.47f, 0.28f), cyan, strokeWidth);
            Vector2[] zigzag = [ArtPoint(0.09f, 0.76f), ArtPoint(0.20f, 0.54f), ArtPoint(0.17f, 0.82f), ArtPoint(0.31f, 0.57f), ArtPoint(0.27f, 0.84f), ArtPoint(0.44f, 0.66f)];
            for (var index = 1; index < zigzag.Length; index++)
            {
                drawList.AddLine(zigzag[index - 1], zigzag[index], yellow, strokeWidth);
            }
            var heart = ArtPoint(0.61f, 0.31f);
            var heartX = artSize.X * 0.085f;
            var heartY = artSize.Y * 0.34f;
            drawList.AddBezierCubic(heart, heart + new Vector2(-heartX, -heartY * 0.55f), heart + new Vector2(-heartX * 1.25f, heartY * 0.32f), heart + new Vector2(0f, heartY), pink, strokeWidth);
            drawList.AddBezierCubic(heart, heart + new Vector2(heartX, -heartY * 0.55f), heart + new Vector2(heartX * 1.25f, heartY * 0.32f), heart + new Vector2(0f, heartY), pink, strokeWidth);
            var smileCenter = ArtPoint(0.86f, 0.55f);
            var smileRadius = Math.Min(artSize.X * 0.10f, artSize.Y * 0.22f);
            drawList.AddCircle(smileCenter, smileRadius, ImGui.GetColorU32(Vector4.One), 32, Math.Max(Ui(2.5f), strokeWidth * 0.8f));
            drawList.AddCircleFilled(smileCenter + new Vector2(-smileRadius * 0.34f, -smileRadius * 0.24f), Math.Max(Ui(1.5f), smileRadius * 0.08f), ImGui.GetColorU32(Vector4.One));
            drawList.AddCircleFilled(smileCenter + new Vector2(smileRadius * 0.34f, -smileRadius * 0.24f), Math.Max(Ui(1.5f), smileRadius * 0.08f), ImGui.GetColorU32(Vector4.One));
            drawList.AddBezierCubic(
                smileCenter + new Vector2(-smileRadius * 0.46f, smileRadius * 0.22f),
                smileCenter + new Vector2(-smileRadius * 0.18f, smileRadius * 0.58f),
                smileCenter + new Vector2(smileRadius * 0.18f, smileRadius * 0.58f),
                smileCenter + new Vector2(smileRadius * 0.46f, smileRadius * 0.22f),
                ImGui.GetColorU32(Vector4.One),
                Math.Max(Ui(2f), strokeWidth * 0.65f));
            drawList.PopClipRect();

            var accentColour = ImGui.GetColorU32(AccentHover);
            var accentY = artMin.Y + artSize.Y * 0.52f;
            drawList.AddLine(new Vector2(artMin.X - Ui(22f), accentY - Ui(9f)), new Vector2(artMin.X - Ui(10f), accentY - Ui(9f)), accentColour, Ui(3f));
            drawList.AddLine(new Vector2(artMin.X - Ui(19f), accentY + Ui(10f)), new Vector2(artMin.X - Ui(8f), accentY + Ui(5f)), accentColour, Ui(3f));
            drawList.AddLine(new Vector2(artMax.X + Ui(8f), accentY - Ui(7f)), new Vector2(artMax.X + Ui(22f), accentY - Ui(13f)), accentColour, Ui(3f));
            drawList.AddLine(new Vector2(artMax.X + Ui(10f), accentY + Ui(9f)), new Vector2(artMax.X + Ui(24f), accentY + Ui(11f)), accentColour, Ui(3f));
        }
        ImGui.EndChild();
    }

    private void DrawWhiteboardModeCard(
        string id,
        float width,
        string badge,
        string title,
        string description,
        IReadOnlyList<string> bullets,
        string buttonLabel,
        WhiteboardMode mode,
        bool isHost,
        bool isViewer)
    {
        var height = Ui(340f);
        using var colours = ImRaii.PushColor(ImGuiCol.ChildBg, CardBg).Push(ImGuiCol.Border, mode == WhiteboardMode.Team ? new Vector4(Accent.X, Accent.Y, Accent.Z, 0.55f) : BorderSubtle);
        using var style = ImRaii.PushStyle(ImGuiStyleVar.ChildRounding, Ui(10f))
            .Push(ImGuiStyleVar.ChildBorderSize, Ui(1f))
            .Push(ImGuiStyleVar.WindowPadding, UiVec(16f, 14f));
        if (UiFonts.Window(ImGui.GetFontSize(), ImGui.BeginChild($"##whiteboardMode{id}", new Vector2(width, height), true, ImGuiWindowFlags.NoScrollbar)))
        {
            DrawWhiteboardModeBadge(badge, mode == WhiteboardMode.Team);
            ImGui.Dummy(UiVec(0f, 13f));

            var rowStart = ImGui.GetCursorScreenPos();
            var iconSize = UiVec(62f, 62f);
            var drawList = ImGui.GetWindowDrawList();
            drawList.AddRectFilled(rowStart, rowStart + iconSize, ImGui.GetColorU32(new Vector4(Accent.X, Accent.Y, Accent.Z, 0.20f)), Ui(10f));
            drawList.AddRect(rowStart, rowStart + iconSize, ImGui.GetColorU32(AccentHover), Ui(10f), ImDrawFlags.None, Ui(1.5f));
            var icon = (mode == WhiteboardMode.Team ? FontAwesomeIcon.Users : FontAwesomeIcon.PaintBrush).ToIconString();
            Vector2 iconTextSize;
            using (UiFonts.PushIcon())
            {
                iconTextSize = ImGui.CalcTextSize(icon) * 1.55f;
            }
            var iconOffset = mode == WhiteboardMode.Team ? UiVec(3f, 0f) : Vector2.Zero;
            UiFonts.DrawIcon(drawList, ImGui.GetFontSize() * 1.55f, rowStart + (iconSize - iconTextSize) * 0.5f + iconOffset, ImGui.GetColorU32(AccentHover), icon);
            var companionIcon = (mode == WhiteboardMode.Team ? FontAwesomeIcon.PaintBrush : FontAwesomeIcon.User).ToIconString();
            UiFonts.DrawIcon(drawList, ImGui.GetFontSize() * 1.35f, rowStart + UiVec(73f, 19f), ImGui.GetColorU32(AccentHover), companionIcon);

            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + Ui(112f));
            using var uiFontScale20179=UiFonts.PushRelative(1.38f);
            ImGui.TextColored(Hex(0xE2E7EF), title);
            uiFontScale20179.Dispose();
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + Ui(112f));
            ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + width - Ui(144f));
            using (UiFonts.PushRelative(.90f)) ImGui.TextColored(MutedText, description);
            ImGui.PopTextWrapPos();
            ImGui.SetCursorScreenPos(new Vector2(rowStart.X, rowStart.Y + iconSize.Y + Ui(15f)));

            foreach (var bullet in bullets)
            {
                DrawWhiteboardFeatureRow(bullet);
            }
            ImGui.SetCursorPosY(height - Ui(68f));
            var canLaunch = !isViewer && CanLaunchActivity(ActiveActivityKind.Whiteboard);
            if (mode == WhiteboardMode.Solo)
                DrawActivityStartButtons("canvasSolo", () => LaunchWhiteboardMode(mode, false),
                    () => LaunchWhiteboardMode(mode, true), canLaunch, 42f);
            else if (DrawWhiteboardLaunchButton(isHost ? "Start in Watch Party" : "Start in party",
                         true, canLaunch))
                LaunchWhiteboardMode(mode, true);
            if (!canLaunch && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            {
                UiFonts.SetTooltip(!CanLaunchActivity(ActiveActivityKind.Whiteboard)
                    ? "End current activity to launch another"
                    : "Only the Watch Party host can start a canvas session.");
            }
        }
        ImGui.EndChild();
    }

    private void DrawWhiteboardHowItWorks() => DrawArcadeSetupSteps("##whiteboardHow", ["Choose a mode", "Draw in the plugin", "See it on your TV"]);

    private void DrawWhiteboardModeBadge(string label, bool collaborative)
    {
        var textSize = ImGui.CalcTextSize(label);
        var size = textSize + UiVec(28f, 15f);
        var origin = ImGui.GetCursorScreenPos();
        ImGui.Dummy(size);
        var drawList = ImGui.GetWindowDrawList();
        var fill = collaborative
            ? AccentActive
            : new Vector4(Accent.X, Accent.Y, Accent.Z, 0.88f);
        drawList.AddRectFilled(origin, origin + size, ImGui.GetColorU32(fill), size.Y * 0.5f);
        drawList.AddText(origin + (size - textSize) * 0.5f, ImGui.GetColorU32(Vector4.One), label);
    }

    private void DrawWhiteboardFeatureRow(string label)
    {
        var origin = ImGui.GetCursorScreenPos();
        var height = Ui(31f);
        ImGui.Dummy(new Vector2(ImGui.GetContentRegionAvail().X, height));
        var drawList = ImGui.GetWindowDrawList();
        var center = origin + UiVec(13f, 14f);
        drawList.AddCircleFilled(center, Ui(12f), ImGui.GetColorU32(Accent));
        var check = FontAwesomeIcon.Check.ToIconString();
        var checkSize = ImGui.CalcTextSize(check);
        UiFonts.DrawIcon(drawList, ImGui.GetFontSize(), center - checkSize * 0.5f, ImGui.GetColorU32(Vector4.One), check);
        drawList.AddText(origin + UiVec(34f, 6f), ImGui.GetColorU32(new Vector4(0.89f, 0.90f, 0.97f, 1f)), label);
    }

    private bool DrawWhiteboardLaunchButton(string label, bool outlined, bool enabled)
    {
        var size = new Vector2(ImGui.GetContentRegionAvail().X, Ui(50f));
        var origin = ImGui.GetCursorScreenPos();
        using var disabled = UiDisabled.Scope(!enabled);
        var clicked = ImGui.InvisibleButton($"##{label}", size);
        var hovered = enabled && ImGui.IsItemHovered();
        var drawList = ImGui.GetWindowDrawList();
        var alpha = enabled ? 1f : 0.42f;
        var fill = outlined
            ? hovered ? AccentFieldHover : AccentFieldBg
            : hovered ? AccentHover : Accent;
        fill.W *= alpha;
        drawList.AddRectFilled(origin, origin + size, ImGui.GetColorU32(fill), Ui(9f));
        if (outlined)
        {
            drawList.AddRect(origin, origin + size, ImGui.GetColorU32(new Vector4(AccentHover.X, AccentHover.Y, AccentHover.Z, alpha)), Ui(9f), ImDrawFlags.None, Ui(2f));
        }
        var textSize = ImGui.CalcTextSize(label) * 1.18f;
        UiFonts.DrawText(drawList, ImGui.GetFontSize() * 1.18f, origin + (size - textSize) * 0.5f, ImGui.GetColorU32(new Vector4(1f, 1f, 1f, alpha)), label);
        return clicked;
    }

    private void DrawWhiteboardFooterNote()
    {
        using var colours = ImRaii.PushColor(ImGuiCol.ChildBg, CardBg).Push(ImGuiCol.Border, BorderSubtle);
        using var style = ImRaii.PushStyle(ImGuiStyleVar.ChildRounding, Ui(9f))
            .Push(ImGuiStyleVar.ChildBorderSize, Ui(1f))
            .Push(ImGuiStyleVar.WindowPadding, UiVec(18f, 13f));
        if (UiFonts.Window(ImGui.GetFontSize(), ImGui.BeginChild("##whiteboardFooterNote", new Vector2(ImGui.GetContentRegionAvail().X, Ui(58f)), true, ImGuiWindowFlags.NoScrollbar)))
        {
            var origin = ImGui.GetCursorScreenPos();
            var icon = FontAwesomeIcon.Tv.ToIconString();
            UiFonts.DrawIcon(ImGui.GetWindowDrawList(), ImGui.GetFontSize() * 1.25f, origin + UiVec(0f, 3f), ImGui.GetColorU32(new Vector4(0.72f, 0.76f, 0.90f, 1f)), icon);
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + Ui(40f));
            ImGui.SetCursorPosY(ImGui.GetCursorPosY() + Ui(4f));
            ImGui.TextColored(MutedText, "Draw directly on the TV or in the plugin window");
        }
        ImGui.EndChild();
    }

    private void LaunchWhiteboardMode(WhiteboardMode mode, bool inParty)
    {
        if (!CanLaunchActivity(ActiveActivityKind.Whiteboard))
        {
            NotifyActivityStartBlocked();
            Plugin.ChatGui.Print("[AlphaChannel] End the active activity before starting a canvas.");
            return;
        }

        pendingWhiteboardMode = mode;
        if (!inParty || stream.Mode == StreamMode.Hosting)
        {
            StartHostWhiteboard(mode);
            return;
        }

        pendingWatchPartyMediaKind = PendingWatchPartyMediaKind.Whiteboard;
        watchPartyCreationPopupOpen = true;
    }

    private void DrawWhiteboardToolbar(bool isHost, bool isViewer)
    {
        var localController = isHost || stream.Mode == StreamMode.None;
        var canUseDrawingTools = localController || (isViewer && whiteboardMode == WhiteboardMode.Team);
        var toolbarHeight = Ui(112f);
        using var toolbarColours = ImRaii.PushColor(ImGuiCol.ChildBg, CardBg)
            .Push(ImGuiCol.Border, BorderSubtle);
        using var toolbarStyle = ImRaii.PushStyle(ImGuiStyleVar.ChildRounding, Ui(10f))
            .Push(ImGuiStyleVar.ChildBorderSize, Ui(1f))
            .Push(ImGuiStyleVar.WindowPadding, UiVec(14f, 12f));

        if (!UiFonts.Window(ImGui.GetFontSize(), ImGui.BeginChild(
                "##whiteboardToolbar",
                new Vector2(ImGui.GetContentRegionAvail().X, toolbarHeight),
                true,
                ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse)))
        {
            ImGui.EndChild();
            return;
        }

        var contentStart = ImGui.GetCursorPos();
        var contentScreenStart = ImGui.GetCursorScreenPos();

        ImGui.SetCursorPos(new Vector2(contentStart.X, contentStart.Y + Ui(27f)));
        DrawWhiteboardStatusBadge(isHost, isViewer);

        var backgroundX = contentStart.X + Ui(110f);
        ImGui.SetCursorPos(new Vector2(backgroundX, contentStart.Y + Ui(2f)));
        using (UiDisabled.Scope(!localController))
        {
            DrawWhiteboardBackgroundControl();
        }

        var colourX = contentStart.X + Ui(234f);
        ImGui.SetCursorPos(new Vector2(colourX, contentStart.Y + Ui(2f)));
        ImGui.TextColored(MutedText, "COLOUR");
        ImGui.SetCursorPos(new Vector2(colourX, contentStart.Y + Ui(24f)));
        using (UiDisabled.Scope(!canUseDrawingTools))
        {
            DrawWhiteboardColourGrid();
        }

        var brushX = contentStart.X + Ui(452f);
        ImGui.SetCursorPos(new Vector2(brushX, contentStart.Y + Ui(2f)));
        ImGui.TextColored(MutedText, "BRUSH SIZE");
        ImGui.SetCursorPos(new Vector2(brushX, contentStart.Y + Ui(24f)));
        using (UiDisabled.Scope(!canUseDrawingTools))
        {
            DrawWhiteboardBrushSizes();
        }

        var actionsX = contentStart.X + Ui(590f);
        ImGui.SetCursorPos(new Vector2(actionsX, contentStart.Y + Ui(18f)));
        DrawWhiteboardActions(localController);

        var drawList = ImGui.GetWindowDrawList();
        var dividerColour = ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 0.09f));
        foreach (var dividerX in new[] { 103f, 227f, 445f, 583f })
        {
            drawList.AddLine(
                contentScreenStart + UiVec(dividerX, 0f),
                contentScreenStart + UiVec(dividerX, 76f),
                dividerColour,
                Ui(1f));
        }

        ImGui.EndChild();
    }

    private void DrawWhiteboardStatusBadge(bool isHost, bool isViewer)
    {
        var active = StandaloneWhiteboardActive && (isHost || isViewer || stream.Mode == StreamMode.None);
        var label = active
            ? whiteboardMode == WhiteboardMode.Team ? "TEAM LIVE" : "SOLO LIVE"
            : "Start";
        var size = UiVec(active ? 96f : 88f, 34f);
        var origin = ImGui.GetCursorScreenPos();
        var canStart = !active && !isViewer && CanLaunchActivity(ActiveActivityKind.Whiteboard);
        bool clicked;
        if (canStart)
        {
            clicked = ImGui.InvisibleButton("##startWhiteboard", size);
        }
        else
        {
            ImGui.Dummy(size);
            clicked = false;
        }
        var drawList = ImGui.GetWindowDrawList();
        if (active)
        {
            drawList.AddRectFilled(
                origin,
                origin + size,
                ImGui.GetColorU32(new Vector4(Good.X, Good.Y, Good.Z, 0.10f)),
                Ui(17f));
            drawList.AddRect(origin, origin + size, ImGui.GetColorU32(Good), Ui(17f), ImDrawFlags.None, Ui(1f));
            drawList.AddCircleFilled(origin + UiVec(13f, 17f), Ui(4.5f), ImGui.GetColorU32(Good));
            drawList.AddText(origin + UiVec(24f, 9f), ImGui.GetColorU32(Good), label);
        }
        else
        {
            var fill = canStart ? Accent : new Vector4(0.20f, 0.21f, 0.27f, 1f);
            var textColour = canStart ? Vector4.One : MutedText;
            drawList.AddRectFilled(origin, origin + size, ImGui.GetColorU32(fill), Ui(8f));
            var textSize = ImGui.CalcTextSize(label);
            drawList.AddText(
                origin + (size - textSize) * 0.5f,
                ImGui.GetColorU32(textColour),
                label);
        }
        if (clicked)
        {
            if (isHost || (pendingWhiteboardMode == WhiteboardMode.Solo && stream.Mode == StreamMode.None))
            {
                StartHostWhiteboard(pendingWhiteboardMode);
            }
            else
            {
                pendingWatchPartyMediaKind = PendingWatchPartyMediaKind.Whiteboard;
                watchPartyCreationPopupOpen = true;
            }
        }
        if (ImGui.IsItemHovered())
        {
            UiFonts.SetTooltip(
                !CanLaunchActivity(ActiveActivityKind.Whiteboard)
                    ? "End current activity to launch another"
                    : isViewer
                    ? "Only the Watch Party host can start the canvas."
                    : isHost
                        ? "Start the canvas on every Watch Party TV."
                        : pendingWhiteboardMode == WhiteboardMode.Solo
                            ? "Start Solo Canvas on your TV."
                            : "Create a Watch Party and start the canvas.");
        }
    }

    private void DrawWhiteboardBackgroundControl()
    {
        ImGui.BeginGroup();
        ImGui.TextColored(MutedText, "Background");
        if (DrawWhiteboardBackgroundButton("White", true))
        {
            SetWhiteboardBackground(true);
        }
        ImGui.SameLine(0f, Ui(4f));
        if (DrawWhiteboardBackgroundButton("Black", false))
        {
            SetWhiteboardBackground(false);
        }
        ImGui.EndGroup();
    }

    private bool DrawWhiteboardBackgroundButton(string label, bool white)
    {
        var selected = whiteboardBackgroundWhite == white;
        var size = UiVec(30f, 30f);
        var origin = ImGui.GetCursorScreenPos();
        ImGui.PushID(label);
        var clicked = ImGui.InvisibleButton("##background", size);
        var hovered = ImGui.IsItemHovered();
        var drawList = ImGui.GetWindowDrawList();
        drawList.AddRectFilled(
            origin,
            origin + size,
            ImGui.GetColorU32(white ? new Vector4(0.96f, 0.97f, 0.99f, 1f) : new Vector4(0.025f, 0.025f, 0.035f, 1f)),
            Ui(8f));
        drawList.AddRect(
            origin,
            origin + size,
            ImGui.GetColorU32(selected ? AccentHover : BorderSubtle),
            Ui(8f),
            ImDrawFlags.None,
            Ui(selected ? 2f : 1f));

        if (selected)
        {
            var check = FontAwesomeIcon.Check.ToIconString();
            var checkSize = ImGui.CalcTextSize(check);
            UiFonts.DrawIcon(drawList, ImGui.GetFontSize(), origin + (size - checkSize) * 0.5f, ImGui.GetColorU32(white ? new Vector4(0.04f, 0.05f, 0.08f, 1f) : Vector4.One), check);
        }
        if (hovered)
        {
            UiFonts.SetTooltip(label);
        }
        ImGui.PopID();
        return clicked;
    }

    private void DrawWhiteboardActions(bool isHost)
    {
        var enabled = isHost && StandaloneWhiteboardActive;
        if (DrawWhiteboardActionTile(
                "undo",
                "Undo",
                FontAwesomeIcon.Undo,
                enabled,
                false))
        {
            UndoWhiteboardStroke();
        }
        ImGui.SameLine(0f, Ui(6f));
        if (DrawWhiteboardActionTile(
                "clear",
                "Clear",
                FontAwesomeIcon.Trash,
                enabled,
                false))
        {
            SendAndApplyWhiteboardCommand($"C|{whiteboardSessionId}");
        }
        ImGui.SameLine(0f, Ui(6f));
        if (DrawWhiteboardActionTile(
                "stop",
                "Stop",
                FontAwesomeIcon.Stop,
                enabled,
                true))
        {
            FinishWhiteboardStroke();
            SendAndApplyWhiteboardCommand($"X|{whiteboardSessionId}");
        }
    }

    private bool DrawWhiteboardActionTile(
        string id,
        string label,
        FontAwesomeIcon icon,
        bool enabled,
        bool danger)
    {
        var size = UiVec(danger ? 62f : 58f, 52f);
        var origin = ImGui.GetCursorScreenPos();
        ImGui.PushID(id);
        using var disabled = UiDisabled.Scope(!enabled);
        var clicked = ImGui.InvisibleButton("##action", size);
        var hovered = enabled && ImGui.IsItemHovered();
        var drawList = ImGui.GetWindowDrawList();
        var baseColour = danger
            ? new Vector4(0.53f, 0.16f, 0.18f, 1f)
            : FrameBg;
        var hoverColour = danger
            ? new Vector4(0.68f, 0.20f, 0.23f, 1f)
            : FrameBgHover;
        var alpha = enabled ? 1f : 0.42f;
        var fill = hovered ? hoverColour : baseColour;
        fill.W *= alpha;
        drawList.AddRectFilled(origin, origin + size, ImGui.GetColorU32(fill), Ui(8f));
        drawList.AddRect(origin, origin + size, ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 0.07f * alpha)), Ui(8f));

        var iconText = icon.ToIconString();
        var iconSize = ImGui.CalcTextSize(iconText);
        UiFonts.DrawIcon(drawList, ImGui.GetFontSize(), origin + new Vector2((size.X - iconSize.X) * 0.5f, Ui(8f)), ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 0.95f * alpha)), iconText);
        var labelSize = ImGui.CalcTextSize(label);
        drawList.AddText(
            origin + new Vector2((size.X - labelSize.X) * 0.5f, Ui(29f)),
            ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 0.92f * alpha)),
            label);
        ImGui.PopID();
        return clicked;
    }

    private void DrawWhiteboardColourGrid()
    {
        const int columns = 8;
        var size = Ui(22f);
        var gap = Ui(4f);
        var drawList = ImGui.GetWindowDrawList();
        var gridStart = ImGui.GetCursorPos();

        for (var index = 0; index < WhiteboardColours.Length; index++)
        {
            var column = index % columns;
            var row = index / columns;
            ImGui.SetCursorPos(gridStart + new Vector2(
                column * (size + gap),
                row * (size + gap)));

            ImGui.PushID(index);
            var origin = ImGui.GetCursorScreenPos();
            if (ImGui.InvisibleButton("##colour", new Vector2(size, size)))
            {
                whiteboardColourIndex = index;
            }
            var selected = index == whiteboardColourIndex;
            var hovered = ImGui.IsItemHovered();
            drawList.AddRectFilled(origin, origin + new Vector2(size, size), ImGui.GetColorU32(WhiteboardColours[index].Value), Ui(6f));
            drawList.AddRect(
                origin - UiVec(selected ? 2f : 0f, selected ? 2f : 0f),
                origin + new Vector2(size, size) + UiVec(selected ? 2f : 0f, selected ? 2f : 0f),
                ImGui.GetColorU32(selected ? AccentHover : hovered ? Vector4.One : BorderSubtle),
                Ui(7f),
                ImDrawFlags.None,
                Ui(selected ? 2.5f : 1f));
            if (selected)
            {
                var check = FontAwesomeIcon.Check.ToIconString();
                var checkSize = ImGui.CalcTextSize(check);
                var checkColour = index is 0 or 3 or 11 or 12 or 15 ? Vector4.One : new Vector4(0.04f, 0.05f, 0.08f, 1f);
                UiFonts.DrawIcon(drawList, ImGui.GetFontSize(), origin + (new Vector2(size, size) - checkSize) * 0.5f, ImGui.GetColorU32(checkColour), check);
            }
            if (ImGui.IsItemHovered())
            {
                UiFonts.SetTooltip(WhiteboardColours[index].Name);
            }

            ImGui.PopID();
        }

        // Set an explicit final cursor position too. ImGui's automatic line
        // handling returns to the child window's left edge after a wrapped
        // row, which previously put row two beneath the status controls.
        ImGui.SetCursorPos(gridStart + new Vector2(0f, (size + gap) * 2f));
    }

    private void DrawWhiteboardBrushSizes()
    {
        var size = Ui(28f);
        var drawList = ImGui.GetWindowDrawList();
        for (var index = 0; index < WhiteboardBrushSizes.Length; index++)
        {
            ImGui.PushID(index);
            var origin = ImGui.GetCursorScreenPos();
            if (ImGui.InvisibleButton("##brushSize", new Vector2(size, size)))
            {
                whiteboardBrushWidth = WhiteboardBrushSizes[index];
            }
            var selected = MathF.Abs(whiteboardBrushWidth - WhiteboardBrushSizes[index]) < 0.1f;
            drawList.AddRectFilled(origin, origin + new Vector2(size, size), ImGui.GetColorU32(selected ? new Vector4(Accent.X, Accent.Y, Accent.Z, 0.28f) : FrameBg), Ui(8f));
            drawList.AddRect(origin, origin + new Vector2(size, size), ImGui.GetColorU32(selected ? AccentHover : BorderSubtle), Ui(8f), ImDrawFlags.None, Ui(selected ? 2f : 1f));
            var radius = Ui(2f + index * 1.8f);
            drawList.AddCircleFilled(origin + new Vector2(size, size) * 0.5f, radius, ImGui.GetColorU32(selected ? WhiteboardColours[whiteboardColourIndex].Value : Vector4.One));
            if (ImGui.IsItemHovered())
            {
                UiFonts.SetTooltip($"{WhiteboardBrushSizes[index]:0} px");
            }
            if (index < WhiteboardBrushSizes.Length - 1)
            {
                ImGui.SameLine(0f, Ui(4f));
            }
            ImGui.PopID();
        }
    }

    private void SetWhiteboardBackground(bool white)
    {
        if (whiteboardActive && whiteboardSessionId is not null)
        {
            SendAndApplyWhiteboardCommand($"B|{whiteboardSessionId}|{(white ? 1 : 0)}");
            return;
        }

        whiteboardBackgroundWhite = white;
        whiteboardTvDirty = true;
    }

    private void DrawWhiteboardCanvas(bool canDraw)
    {
        var available = ImGui.GetContentRegionAvail();
        var width = Math.Max(Ui(320f), available.X);
        var height = Math.Min(width * 9f / 16f, Math.Max(Ui(260f), available.Y - Ui(30f)));
        width = height * 16f / 9f;
        var canvasSize = new Vector2(width, height);
        var origin = ImGui.GetCursorScreenPos();
        var max = origin + canvasSize;
        var drawList = ImGui.GetWindowDrawList();

        ImGui.InvisibleButton(
            "##whiteboardCanvas",
            canvasSize,
            ImGuiButtonFlags.MouseButtonLeft);

        var background = whiteboardBackgroundWhite
            ? new Vector4(1f, 1f, 1f, 1f)
            : new Vector4(0.025f, 0.028f, 0.04f, 1f);
        drawList.AddRectFilled(origin, max, ImGui.GetColorU32(background), Ui(8f));
        drawList.PushClipRect(origin, max, true);

        foreach (var stroke in VisibleWhiteboardStrokes)
        {
            var color = ImGui.GetColorU32(WhiteboardColours[stroke.ColourIndex].Value);
            for (var index = 1; index < stroke.Points.Count; index++)
            {
                drawList.AddLine(
                    origin + stroke.Points[index - 1] * canvasSize,
                    origin + stroke.Points[index] * canvasSize,
                    color,
                    Math.Max(1f, Ui(stroke.Width) * canvasSize.Y / WhiteboardRenderer.Height));
            }
        }

        if (canDraw)
        {
            var chipSize = UiVec(148f, 30f);
            var chipOrigin = origin + UiVec(12f, 12f);
            drawList.AddRectFilled(
                chipOrigin,
                chipOrigin + chipSize,
                ImGui.GetColorU32(new Vector4(0.035f, 0.045f, 0.075f, 0.92f)),
                Ui(15f));
            drawList.AddRect(
                chipOrigin,
                chipOrigin + chipSize,
                ImGui.GetColorU32(BorderSubtle),
                Ui(15f));
            drawList.AddCircleFilled(
                chipOrigin + UiVec(15f, 15f),
                Ui(5f),
                ImGui.GetColorU32(WhiteboardColours[whiteboardColourIndex].Value));
            drawList.AddText(
                chipOrigin + UiVec(28f, 7f),
                ImGui.GetColorU32(new Vector4(0.84f, 0.86f, 0.94f, 1f)),
                $"{GetWhiteboardBrushName()} brush");
        }

        drawList.PopClipRect();
        drawList.AddRect(origin, max, ImGui.GetColorU32(BorderSubtle), Ui(8f), ImDrawFlags.None, Ui(1f));

        if (!canDraw || !ImGui.IsItemHovered())
        {
            if (!ImGui.IsMouseDown(ImGuiMouseButton.Left))
            {
                FinishWhiteboardStroke();
            }
            return;
        }

        var mouse = ImGui.GetMousePos();
        var point = new Vector2(
            Math.Clamp((mouse.X - origin.X) / canvasSize.X, 0f, 1f),
            Math.Clamp((mouse.Y - origin.Y) / canvasSize.Y, 0f, 1f));

        if (ImGui.IsMouseClicked(ImGuiMouseButton.Left))
        {
            BeginWhiteboardStroke(point);
        }
        else if (ImGui.IsMouseDown(ImGuiMouseButton.Left) && whiteboardDrawingStrokeId is not null)
        {
            ContinueWhiteboardStroke(point);
        }

        if (ImGui.IsMouseReleased(ImGuiMouseButton.Left))
        {
            FinishWhiteboardStroke();
        }
    }

    private void StartHostWhiteboard(WhiteboardMode mode, ActiveActivityKind owner = ActiveActivityKind.Whiteboard)
    {
        if (!IsLocalActivityHost)
        {
            return;
        }
        if (!CanLaunchActivity(owner))
        {
            NotifyActivityStartBlocked();
            Plugin.ChatGui.Print("[AlphaChannel] End the active activity before starting a canvas.");
            return;
        }

        var sessionId = Guid.NewGuid().ToString("N");
        ResetWhiteboard(sessionId, whiteboardBackgroundWhite);
        whiteboardRequiresRelay = owner == ActiveActivityKind.Whiteboard && stream.Mode == StreamMode.Hosting;
        whiteboardMode = mode;
        whiteboardActive = true;
        screenController.Engine.StartWhiteboard();
        SendAndApplyWhiteboardCommand(BuildWhiteboardActivationCommand());
        if (owner == ActiveActivityKind.Whiteboard && stream.Mode == StreamMode.Hosting) TrySendRemainingState("canvas", "");
        whiteboardRosterSignature = string.Empty;
    }

    private void FinishWhiteboardStroke()
    {
        if (whiteboardDrawingStrokeId is null)
        {
            return;
        }

        if (!FlushWhiteboardStrokeBatch(true)) return;
        whiteboardDrawingStrokeId = null;
        whiteboardPendingPoints.Clear();
    }

    private bool FlushWhiteboardStrokeBatch(bool complete)
    {
        if (whiteboardDrawingStrokeId is null || whiteboardPendingPoints.Count == 0 || whiteboardSessionId is null)
        {
            return true;
        }

        var encodedPoints = EncodeWhiteboardPoints(whiteboardPendingPoints);
        var command = string.Join(
            '|',
            "P",
            whiteboardSessionId,
            whiteboardDrawingStrokeId,
            whiteboardDrawingSequence.ToString(CultureInfo.InvariantCulture),
            complete ? "1" : "0",
            whiteboardColourIndex.ToString(CultureInfo.InvariantCulture),
            whiteboardBrushWidth.ToString("0.##", CultureInfo.InvariantCulture),
            encodedPoints);
        // Pictionary is always a shared canvas. Keep routing the current drawer's
        // strokes through the host even if a delayed canvas snapshot temporarily
        // left the local whiteboard mode stale.
        if (stream.Mode == StreamMode.Viewing &&
            (whiteboardMode == WhiteboardMode.Team || (PictionaryActive && LocalIsPictionaryDrawer) ||
             (PictionaryTeamsActive && TeamsLocalDrawerColour is 0 or 1)))
        {
            if (!SendDrawingContribution("Q|" + command)) return false;
        }
        else
        {
            SendAndApplyWhiteboardCommand(command);
        }
        if (whiteboardDrawingStrokeId is { } previewId && whiteboardLocalInk.TryGetValue(previewId, out var preview))
            preview.LastSequence = whiteboardDrawingSequence;
        whiteboardDrawingSequence++;

        var last = whiteboardPendingPoints[^1];
        whiteboardPendingPoints.Clear();
        if (!complete)
        {
            // Repeat the join point so independently received batches form one continuous curve.
            whiteboardPendingPoints.Add(last);
        }
        whiteboardLastBatchUtc = DateTime.UtcNow;
        return true;
    }

    private void UndoWhiteboardStroke()
    {
        var stroke = whiteboardStrokes.LastOrDefault();
        if (stroke is not null && whiteboardSessionId is not null)
        {
            SendAndApplyWhiteboardCommand($"U|{whiteboardSessionId}|{stroke.Id}");
        }
    }

    private void SendAndApplyWhiteboardCommand(string command)
    {
        ApplyWhiteboardCommand(command);
        if (stream.Mode == StreamMode.Hosting)
        {
            if (!command.StartsWith("X|", StringComparison.Ordinal) && DrawingRelayGame is { } game && GameRelay(game) is not null) PublishDrawingState();
            else _ = stream.SendWhiteboardCommandAsync(command);
        }
    }

    private string BuildWhiteboardActivationCommand() =>
        $"A|{whiteboardSessionId}|{(whiteboardBackgroundWhite ? 1 : 0)}|{(whiteboardMode == WhiteboardMode.Team ? "T" : "S")}{(whiteboardRequiresRelay ? "|W" : "")}";

    private void TryRelayWhiteboardContribution(string senderId, string command)
    {
        if (!whiteboardActive ||
            whiteboardMode != WhiteboardMode.Team ||
            whiteboardSessionId is null ||
            !stream.Roster.Any(member => string.Equals(member.UserId, senderId, StringComparison.Ordinal)) ||
            (PictionaryActive && !string.Equals(senderId, pictionaryDrawerId, StringComparison.Ordinal)) ||
            (PictionaryTeamsActive && (teamsPhase != TeamsPhase.Drawing ||
                !teamsDrawers.Contains(senderId, StringComparer.Ordinal))))
        {
            return;
        }

        var now = DateTime.UtcNow;
        if (whiteboardContributionRates.TryGetValue(senderId, out var rate) &&
            now - rate.WindowStart < TimeSpan.FromSeconds(1))
        {
            if (rate.Count >= 24)
            {
                return;
            }
            whiteboardContributionRates[senderId] = (rate.WindowStart, rate.Count + 1);
        }
        else
        {
            whiteboardContributionRates[senderId] = (now, 1);
        }

        var parts = command.Split('|');
        if (!PictionaryTeamsActive && parts.Length == 4 && parts[0] == "Q" && parts[1] == "U" &&
            string.Equals(parts[2], whiteboardSessionId, StringComparison.Ordinal) &&
            whiteboardStrokeById.ContainsKey(parts[3]))
        {
            SendAndApplyWhiteboardCommand($"U|{whiteboardSessionId}|{parts[3]}");
            return;
        }
        if (!PictionaryTeamsActive && parts.Length == 3 && parts[0] == "Q" && parts[1] == "C" &&
            string.Equals(parts[2], whiteboardSessionId, StringComparison.Ordinal))
        {
            SendAndApplyWhiteboardCommand($"C|{whiteboardSessionId}");
            return;
        }
        if (parts.Length != 9 || parts[0] != "Q" || parts[1] != "P" ||
            !string.Equals(parts[2], whiteboardSessionId, StringComparison.Ordinal) ||
            !Guid.TryParseExact(parts[3], "N", out _) ||
            !int.TryParse(parts[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out var sequence) ||
            sequence < 0 ||
            !int.TryParse(parts[6], NumberStyles.Integer, CultureInfo.InvariantCulture, out var colourIndex) ||
            colourIndex < 0 || colourIndex >= WhiteboardColours.Length ||
            !float.TryParse(parts[7], NumberStyles.Float, CultureInfo.InvariantCulture, out var width) ||
            !WhiteboardBrushSizes.Any(size => MathF.Abs(size - width) < 0.1f) ||
            DecodeWhiteboardPoints(parts[8]).Count is 0 or > 64)
        {
            return;
        }

        if (PictionaryTeamsActive)
        {
            var team = string.Equals(senderId, teamsDrawers[0], StringComparison.Ordinal) ? 0 : 1;
            var points = DecodeWhiteboardPoints(parts[8]);
            if (points.Any(point => !float.IsFinite(point.X) || !float.IsFinite(point.Y) ||
                point.Y < 0.085f || point.Y > 0.84f ||
                (team == 0 ? point.X < 0.02f || point.X > 0.48f : point.X < 0.52f || point.X > 0.98f)))
                return;
        }

        var senderKey = unchecked((uint)StringComparer.Ordinal.GetHashCode(senderId)).ToString("X8", CultureInfo.InvariantCulture);
        var authoritative = string.Join(
            '|',
            "P",
            whiteboardSessionId,
            DrawingRelayGame is { } relayGame && GameRelay(relayGame) is not null ? parts[3] : senderKey + parts[3],
            parts[4],
            parts[5] == "1" ? "1" : "0",
            parts[6],
            parts[7],
            parts[8]);
        SendAndApplyWhiteboardCommand(authoritative);
    }

    private void SendWhiteboardSnapshot()
    {
        if (DrawingRelayGame is { } game && GameRelay(game) is not null) { PublishDrawingState(); return; }
        if (!whiteboardActive || whiteboardSessionId is null)
        {
            return;
        }

        // A new Pictionary participant may have no history. Mark this replay as an
        // authoritative snapshot so existing viewers reset before receiving the same strokes;
        // this avoids duplicate batch tails while bringing the newcomer fully up to date.
        _ = stream.SendWhiteboardCommandAsync(
            $"R|{whiteboardSessionId}|{(whiteboardBackgroundWhite ? 1 : 0)}|{(whiteboardMode == WhiteboardMode.Team ? "T" : "S")}");

        foreach (var stroke in whiteboardStrokes)
        {
            for (var offset = 0; offset < stroke.Points.Count; offset += WhiteboardPointBatchSize)
            {
                var points = stroke.Points.Skip(offset).Take(WhiteboardPointBatchSize).ToList();
                if (offset > 0)
                {
                    points.Insert(0, stroke.Points[offset - 1]);
                }
                var sequence = offset / WhiteboardPointBatchSize;
                var complete = offset + WhiteboardPointBatchSize >= stroke.Points.Count;
                _ = stream.SendWhiteboardCommandAsync(string.Join(
                    '|',
                    "P",
                    whiteboardSessionId,
                    stroke.Id,
                    sequence.ToString(CultureInfo.InvariantCulture),
                    complete ? "1" : "0",
                    stroke.ColourIndex.ToString(CultureInfo.InvariantCulture),
                    stroke.Width.ToString("0.##", CultureInfo.InvariantCulture),
                    EncodeWhiteboardPoints(points)));
            }
        }
    }

    private void ApplyWhiteboardCommand(string command)
    {
        var parts = command.Split('|');
        if (parts.Length < 2)
        {
            return;
        }

        // The global canvas never accepts ink or snapshots from a party host.
        if (pixelsActive && parts[0] is not ("A" or "X")) return;
        switch (parts[0])
        {
            case "A" when parts.Length is 3 or 4 or 5:
            {
                var backgroundWhite = parts[2] == "1";
                var mode = parts.Length >= 4 && parts[3] == "T"
                    ? WhiteboardMode.Team
                    : WhiteboardMode.Solo;
                if (!string.Equals(parts[1], whiteboardSessionId, StringComparison.Ordinal))
                {
                    ResetWhiteboard(parts[1], backgroundWhite);
                }
                else
                {
                    whiteboardBackgroundWhite = backgroundWhite;
                }
                whiteboardMode = mode;
                whiteboardRequiresRelay = parts.Length == 5 && parts[4] == "W";
                whiteboardActive = true;
                whiteboardTvDirty = true;
                if (mode == WhiteboardMode.Team)
                    RequestTeamWhiteboardPrompt(parts[1]);
                break;
            }
            case "R" when parts.Length is 3 or 4:
                ResetWhiteboard(parts[1], parts[2] == "1");
                whiteboardMode = parts.Length == 4 && parts[3] == "T"
                    ? WhiteboardMode.Team
                    : WhiteboardMode.Solo;
                whiteboardActive = true;
                whiteboardTvDirty = true;
                if (whiteboardMode == WhiteboardMode.Team)
                    RequestTeamWhiteboardPrompt(parts[1]);
                break;
            case "B" when parts.Length == 3 && SessionMatches(parts[1]):
                whiteboardBackgroundWhite = parts[2] == "1";
                whiteboardTvDirty = true;
                break;
            case "C" when SessionMatches(parts[1]):
                if (DrawingRelayGame is { } game) activityRelayErrors.Remove(game);
                whiteboardLocalInk.Clear();
                whiteboardStrokes.Clear();
                whiteboardStrokeById.Clear();
                whiteboardTvDirty = true;
                break;
            case "U" when parts.Length == 3 && SessionMatches(parts[1]):
                if (whiteboardStrokeById.Remove(parts[2], out var removed))
                {
                    whiteboardStrokes.Remove(removed);
                    whiteboardTvDirty = true;
                }
                break;
            case "X" when parts.Length == 2 && SessionMatches(parts[1]):
                HandleWhiteboardClosed();
                whiteboardDrawingStrokeId = null;
                whiteboardPendingPoints.Clear();
                if (screenController.Engine.IsPlayingWhiteboard)
                {
                    screenController.Engine.StopVideo();
                }
                break;
            case "P" when parts.Length == 8 && SessionMatches(parts[1]):
                ApplyWhiteboardPointBatch(parts);
                break;
        }
    }

    private void HandleWhiteboardClosed()
    {
        ResetBlastPartyLocal();
        ResetPixelsLocal();
        ResetRemainingRelay("canvas");
        UpdateCanvasActivityPoints();
        whiteboardActive = false;
        ResetCanvasActivityPointsClock();
        if (ChocoRaceActive &&
            string.Equals(whiteboardSessionId, chocoRaceWhiteboardSessionId, StringComparison.Ordinal))
            ResetChocoRaceLocal();
        if (BingoActive &&
            (stream.Mode == StreamMode.Viewing ||
             string.Equals(whiteboardSessionId, bingoWhiteboardSessionId, StringComparison.Ordinal)))
            ResetBingoLocal();
        if (SkyRailActive &&
            (stream.Mode == StreamMode.Viewing ||
             string.Equals(whiteboardSessionId, skyRailWhiteboardSessionId, StringComparison.Ordinal)))
            ResetSkyRailLocal();
        if (LuckyWheelActive && (stream.Mode == StreamMode.Viewing || whiteboardSessionId == luckyWheelState?.CanvasId))
            ResetLuckyWheelLocal();
        if (SlotMachineActive &&
            (stream.Mode == StreamMode.Viewing ||
             string.Equals(whiteboardSessionId, slotMachineWhiteboardSessionId, StringComparison.Ordinal)))
            ResetSlotMachineLocal();
        if (pokerSessionId is not null &&
            (stream.Mode == StreamMode.Viewing ||
             string.Equals(whiteboardSessionId, pokerWhiteboardSessionId, StringComparison.Ordinal)))
            ResetPokerLocal();

        // Pictionary owns a team-whiteboard session internally. Its own lifecycle returns the
        // activity to the Pictionary setup page, so only invalidate a standalone Whiteboard.
        if (PictionaryActive || PictionaryTeamsActive)
        {
            return;
        }

        if (lastActivitiesPage == HomePage.Whiteboard)
        {
            lastActivitiesPage = HomePage.Arcade;
        }
        if (currentPage == HomePage.Whiteboard)
        {
            currentPage = HomePage.Arcade;
        }
    }

    private void ApplyWhiteboardPointBatch(string[] parts)
    {
        if (!int.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var sequence) ||
            !int.TryParse(parts[5], NumberStyles.Integer, CultureInfo.InvariantCulture, out var colourIndex) ||
            !float.TryParse(parts[6], NumberStyles.Float, CultureInfo.InvariantCulture, out var width))
        {
            return;
        }

        var points = DecodeWhiteboardPoints(parts[7]);
        if (points.Count == 0) return;
        if (CloudflareDrawing && whiteboardStrokes.Sum(s => s.Points.Count) + points.Count > 16384)
        { if (DrawingRelayGame is { } fullGame) activityRelayErrors[fullGame] = "Canvas is full. Clear the drawing to continue."; return; }

        if (!whiteboardStrokeById.TryGetValue(parts[2], out var stroke))
        {
            if (CloudflareDrawing && whiteboardStrokes.Count >= 128)
            { if (DrawingRelayGame is { } fullGame) activityRelayErrors[fullGame] = "Canvas is full. Clear the drawing to continue."; return; }
            if (sequence != 0)
            {
                return;
            }
            stroke = new WhiteboardStroke
            {
                Id = parts[2],
                Started = remainingRelayRevisions.GetValueOrDefault(DrawingRelayGame ?? "") + 1,
                ColourIndex = Math.Clamp(colourIndex, 0, WhiteboardColours.Length - 1),
                Width = Math.Clamp(width, 1f, 48f),
            };
            whiteboardStrokeById.Add(stroke.Id, stroke);
            whiteboardStrokes.Add(stroke);
        }

        if (sequence <= stroke.LastSequence || sequence != stroke.LastSequence + 1)
        {
            return;
        }

        stroke.Points.AddRange(points);
        stroke.LastSequence = sequence;
        stroke.Complete = parts[4] == "1";
        whiteboardTvDirty = true;
    }

    private bool SessionMatches(string sessionId) =>
        whiteboardActive && string.Equals(sessionId, whiteboardSessionId, StringComparison.Ordinal);

    private void ResetWhiteboard(string sessionId, bool backgroundWhite)
    {
        if (pixelsActive && sessionId != pixelsCanvas) ResetPixelsLocal();
        if (ChocoRaceActive &&
            !string.Equals(sessionId, chocoRaceWhiteboardSessionId, StringComparison.Ordinal))
            ResetChocoRaceLocal();
        if (LuckyWheelActive && sessionId != luckyWheelState?.CanvasId) ResetLuckyWheelLocal();
        if (SlotMachineActive && slotMachineWhiteboardSessionId is not null &&
            !string.Equals(sessionId, slotMachineWhiteboardSessionId, StringComparison.Ordinal))
            ResetSlotMachineLocal();
        if (pokerSessionId is not null &&
            !string.Equals(sessionId, whiteboardSessionId, StringComparison.Ordinal))
            ResetPokerLocal();
        whiteboardSessionId = sessionId;
        whiteboardRequiresRelay = false;
        whiteboardBackgroundWhite = backgroundWhite;
        whiteboardMode = WhiteboardMode.Solo;
        whiteboardLocalInk.Clear();
        whiteboardStrokes.Clear();
        whiteboardStrokeById.Clear();
        whiteboardContributionRates.Clear();
        whiteboardPendingPoints.Clear();
        whiteboardDrawingStrokeId = null;
        whiteboardTvDirty = true;
    }

    private void RenderWhiteboardTvIfNeeded()
    {
        if (pixelsActive && !screenController.Engine.IsActive && IsMiniPlayerOpen?.Invoke() != true &&
            !(HasVisiblePluginWindow && currentPage == HomePage.Pixels)) return;
        // Animated games own their frame cadence and publish the same finished
        // frame to each visible surface. Do not redraw them in the canvas loop.
        if (BlastPartyActive || BingoActive || SkyRailActive || RouletteActive || ChocoRaceActive)
            return;
        if (!whiteboardActive || !whiteboardTvDirty)
        {
            return;
        }

        if (stream.Mode == StreamMode.Viewing &&
            !ViewerTvEnabled &&
            IsMiniPlayerOpen?.Invoke() != true)
        {
            if (pixelsActive && HasVisiblePluginWindow && currentPage == HomePage.Pixels || QuizActive && IsOpen && currentPage == HomePage.Quiz)
            {
                screenController.Engine.UpdateWhiteboardPreview(BuildWhiteboardPixels());
                whiteboardTvDirty = false;
            }
            return;
        }

        screenController.Engine.UpdateWhiteboard(BuildWhiteboardPixels());
        whiteboardTvDirty = false;
    }

    private readonly byte[] whiteboardFramePixels = new byte[WhiteboardRenderer.Width * WhiteboardRenderer.Height * 4];

    private byte[] BuildWhiteboardPixels()
    {
        var width = WhiteboardRenderer.Width;
        var height = WhiteboardRenderer.Height;
        var background = whiteboardBackgroundWhite ? (byte)255 : (byte)7;
        var pixels = whiteboardFramePixels;
        for (var index = 0; index < pixels.Length; index += 4)
        {
            pixels[index] = background;
            pixels[index + 1] = background;
            pixels[index + 2] = background;
            pixels[index + 3] = 255;
        }

        if (!BlastPartyActive && !pixelsActive && !QuizActive && !RouletteActive && !BingoActive && !SkyRailActive && !SlotMachineActive && !LuckyWheelActive && !PokerActive && !ChocoRaceActive)
        {
            foreach (var stroke in VisibleWhiteboardStrokes)
            {
                var color = WhiteboardColours[stroke.ColourIndex].Rgb;
                for (var index = 1; index < stroke.Points.Count; index++)
                {
                    DrawWhiteboardLine(
                        pixels,
                        width,
                        height,
                        stroke.Points[index - 1],
                        stroke.Points[index],
                        Math.Max(1, (int)MathF.Round(stroke.Width)),
                        color);
                }
            }
        }
        if (pixelsActive)
        {
            ApplyPixelsTvOverlay(pixels);
        }
        else if (QuizActive)
        {
            ApplyQuizTvOverlay(pixels, width, height);
        }
        else if (PictionaryActive)
        {
            ApplyPictionaryTvOverlay(pixels, width, height);
        }
        else if (PictionaryTeamsActive)
        {
            ApplyPictionaryTeamsTvOverlay(pixels, width, height);
            if (LocalCanDrawPictionaryOnTv) DrawPictionaryTvDrawingToolbar(pixels, width, height);
            DrawTeamsDrawerTvNotification(pixels, width, height);
        }
        else if (RouletteActive)
        {
            ApplyRouletteTvOverlay(pixels, width, height);
        }
        else if (BingoActive)
        {
            ApplyBingoTvOverlay(pixels, width, height);
        }
        else if (BlastPartyActive)
        {
            RenderBlastParty(pixels);
        }
        else if (SkyRailActive)
        {
            ApplySkyRailTvOverlay(pixels, width, height);
        }
        else if (LuckyWheelActive)
        {
            ApplyLuckyWheelTvOverlay(pixels, width, height);
        }
        else if (SlotMachineActive)
        {
            ApplySlotMachineTvOverlay(pixels, width, height);
        }
        else if (PokerActive)
        {
            ApplyPokerTvOverlay(pixels, width, height);
        }
        else if (ChocoRaceActive)
        {
            var racePixels = BuildRacePreviewPixels(ChocoRaceNowUtc);
            Buffer.BlockCopy(racePixels, 0, pixels, 0, Math.Min(racePixels.Length, pixels.Length));
        }
        else if (LocalCanDrawPictionaryOnTv)
        {
            DrawPictionaryTvDrawingToolbar(pixels, width, height);
        }
        return pixels;
    }

    private static void DrawWhiteboardLine(
        byte[] pixels,
        int width,
        int height,
        Vector2 from,
        Vector2 to,
        int thickness,
        uint color)
    {
        var x0 = (int)MathF.Round(from.X * (width - 1));
        var y0 = (int)MathF.Round(from.Y * (height - 1));
        var x1 = (int)MathF.Round(to.X * (width - 1));
        var y1 = (int)MathF.Round(to.Y * (height - 1));
        var dx = x1 - x0;
        var dy = y1 - y0;
        var steps = Math.Max(Math.Abs(dx), Math.Abs(dy));
        var radius = Math.Max(1, thickness / 2);

        for (var step = 0; step <= Math.Max(1, steps); step++)
        {
            var progress = steps == 0 ? 0f : step / (float)steps;
            var x = (int)MathF.Round(x0 + dx * progress);
            var y = (int)MathF.Round(y0 + dy * progress);
            for (var oy = -radius; oy <= radius; oy++)
            for (var ox = -radius; ox <= radius; ox++)
            {
                if (ox * ox + oy * oy > radius * radius)
                {
                    continue;
                }
                var px = x + ox;
                var py = y + oy;
                if (px < 0 || py < 0 || px >= width || py >= height)
                {
                    continue;
                }
                var pixel = (py * width + px) * 4;
                pixels[pixel] = (byte)(color & 0xFF);
                pixels[pixel + 1] = (byte)((color >> 8) & 0xFF);
                pixels[pixel + 2] = (byte)((color >> 16) & 0xFF);
                pixels[pixel + 3] = 255;
            }
        }
    }

    private string GetWhiteboardBrushName()
    {
        var index = Array.FindIndex(
            WhiteboardBrushSizes,
            size => MathF.Abs(size - whiteboardBrushWidth) < 0.1f);
        return index switch
        {
            0 => "Small",
            1 => "Medium",
            2 => "Large",
            3 => "Extra large",
            _ => $"{whiteboardBrushWidth:0} px",
        };
    }

    private static string EncodeWhiteboardPoints(IReadOnlyList<Vector2> points)
    {
        var bytes = new byte[points.Count * 4];
        for (var index = 0; index < points.Count; index++)
        {
            var x = (ushort)Math.Clamp((int)MathF.Round(points[index].X * ushort.MaxValue), 0, ushort.MaxValue);
            var y = (ushort)Math.Clamp((int)MathF.Round(points[index].Y * ushort.MaxValue), 0, ushort.MaxValue);
            bytes[index * 4] = (byte)x;
            bytes[index * 4 + 1] = (byte)(x >> 8);
            bytes[index * 4 + 2] = (byte)y;
            bytes[index * 4 + 3] = (byte)(y >> 8);
        }
        return Convert.ToBase64String(bytes);
    }

    private static List<Vector2> DecodeWhiteboardPoints(string encoded)
    {
        try
        {
            var bytes = Convert.FromBase64String(encoded);
            if (bytes.Length == 0 || bytes.Length % 4 != 0 || bytes.Length > 1024)
            {
                return [];
            }
            var points = new List<Vector2>(bytes.Length / 4);
            for (var index = 0; index < bytes.Length; index += 4)
            {
                var x = (ushort)(bytes[index] | bytes[index + 1] << 8);
                var y = (ushort)(bytes[index + 2] | bytes[index + 3] << 8);
                points.Add(new Vector2(x / (float)ushort.MaxValue, y / (float)ushort.MaxValue));
            }
            return points;
        }
        catch (FormatException)
        {
            return [];
        }
    }
}

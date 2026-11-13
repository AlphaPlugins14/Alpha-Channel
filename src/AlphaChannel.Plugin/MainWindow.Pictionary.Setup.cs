using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private static readonly string[] PictionaryNextDrawerOptions =
    [
        "First correct guesser",
        "Random player",
    ];

    private void OpenPictionarySetup()
    {
        if (!CanLaunchActivity(ActiveActivityKind.Pictionary))
            return;
        pictionaryShowSetup = !PictionaryActive;
        lastActivitiesPage = HomePage.Pictionary;
        currentPage = HomePage.Pictionary;
    }

    private void DrawPictionarySetupPage()
    {
        using var spacing = ImRaii.PushStyle(ImGuiStyleVar.ItemSpacing, UiVec(10f, 12f));

        DrawPictionarySetupHero();

        var available = ImGui.GetContentRegionAvail().X;
        var gap = Ui(12f);
        var stacked = available < Ui(760f);
        var cardWidth = stacked ? available : (available - gap) * 0.5f;

        DrawPictionaryStandardRulesCard(cardWidth, Ui(380f));
        if (!stacked)
        {
            ImGui.SameLine(0f, gap);
        }
        DrawPictionaryCustomRulesCard(cardWidth, Ui(390f));

        DrawPictionarySetupSteps();
        DrawPictionarySetupInfo();
        ImGui.Dummy(UiVec(0f, 12f));
    }

    private void DrawPictionarySetupHero() => DrawArcadeReviewHero("pictionary", "Pictionary", "Draw, guess, and race to the top", "Take turns drawing one of three words while everyone guesses on the TV or Pictionary page.", "1 host + 1 viewer required", true, null);


    private void DrawPictionaryStandardRulesCard(float width, float height)
    {
        using var colours = ImRaii.PushColor(ImGuiCol.ChildBg, CardBg).Push(ImGuiCol.Border, BorderSubtle);
        using var style = ImRaii.PushStyle(ImGuiStyleVar.ChildRounding, Ui(10f))
            .Push(ImGuiStyleVar.ChildBorderSize, Ui(1f))
            .Push(ImGuiStyleVar.WindowPadding, UiVec(18f, 16f));

        if (UiFonts.Window(ImGui.GetFontSize(), ImGui.BeginChild("##pictionaryStandardRules", new Vector2(width, height), true, ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse)))
        {
            ImGui.SetScrollY(0f);
            var origin = ImGui.GetWindowPos();
            DrawPictionarySetupBadge("Recommended", true);
            ImGui.Dummy(UiVec(0f, 8f));
            DrawPictionarySetupCardHeading(FontAwesomeIcon.PaintBrush, "Standard rules", "Jump straight into a balanced 10-round game.");
            ImGui.Dummy(UiVec(0f, 11f));
            DrawActivityFeature("10 rounds • 60 seconds");
            DrawActivityFeature("First correct: 3 points • Others: 1 point");
            DrawActivityFeature("First correct guesser draws next");
            DrawActivityFeature("Random drawer if nobody guesses");

            var enabled = CanRequestPictionaryStart();
            var label = PictionarySetupStartLabel("Start with Standard Rules");
            ImGui.SetCursorScreenPos(new Vector2(origin.X + Ui(18f), origin.Y + height - Ui(52f)));
            if (DrawPictionarySetupStartButton("standard", label, enabled))
            {
                ApplyPictionaryStandardRules();
                RequestPictionaryStart();
            }
        }
        ImGui.EndChild();
    }

    private void DrawPictionaryCustomRulesCard(float width, float height)
    {
        using var colours = ImRaii.PushColor(ImGuiCol.ChildBg, CardBg)
            .Push(ImGuiCol.Border, new Vector4(AccentHover.X, AccentHover.Y, AccentHover.Z, 0.75f));
        using var style = ImRaii.PushStyle(ImGuiStyleVar.ChildRounding, Ui(10f))
            .Push(ImGuiStyleVar.ChildBorderSize, Ui(1f))
            .Push(ImGuiStyleVar.WindowPadding, UiVec(18f, 16f));

        if (UiFonts.Window(ImGui.GetFontSize(), ImGui.BeginChild("##pictionaryCustomRules", new Vector2(width, height), true, ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse)))
        {
            ImGui.SetScrollY(0f);
            var origin = ImGui.GetWindowPos();
            DrawPictionarySetupBadge("Custom game", false);
            ImGui.SetCursorScreenPos(origin + UiVec(18f, 70f));
            DrawPictionarySetupCardHeading(FontAwesomeIcon.Cog, "Custom rules", "Adjust the length, scoring, timer, and turn order.");
            ImGui.SetCursorScreenPos(origin + UiVec(18f, 138f));

            var canEdit = stream.Mode != StreamMode.Viewing &&
                CanLaunchActivity(ActiveActivityKind.Pictionary);
            using (UiDisabled.Scope(!canEdit))
            using (ImRaii.PushStyle(ImGuiStyleVar.FrameRounding, Ui(7f)))
            {
                DrawPictionaryRuleStepper("Rounds", "rounds", ref pictionaryCustomRoundCount, 3, 20, 1, value => value.ToString());
                DrawPictionaryRuleStepper("Round timer", "timer", ref pictionaryCustomDrawingSeconds, 30, 120, 15, value => $"{value} seconds");
                DrawPictionaryRuleStepper("First guess", "firstPoints", ref pictionaryCustomFirstGuessPoints, 0, 10, 1, PictionaryPointsLabel);
                DrawPictionaryRuleStepper("Other guesses", "otherPoints", ref pictionaryCustomOtherGuessPoints, 0, 10, 1, PictionaryPointsLabel);
                DrawPictionaryRuleStepper("Drawer reward", "drawerPoints", ref pictionaryCustomDrawerPoints, 0, 10, 1, PictionaryPointsLabel);
                DrawPictionaryNextDrawerRule();
            }

            var enabled = CanRequestPictionaryStart();
            var label = PictionarySetupStartLabel("Start Custom Game");
            ImGui.SetCursorScreenPos(new Vector2(origin.X + Ui(18f), origin.Y + height - Ui(52f)));
            if (DrawPictionarySetupStartButton("custom", label, enabled))
            {
                ApplyPictionaryCustomRules();
                RequestPictionaryStart();
            }
        }
        ImGui.EndChild();
    }

    private void DrawPictionarySetupCardHeading(FontAwesomeIcon icon, string title, string description)
    {
        var origin = ImGui.GetCursorScreenPos();
        var iconSize = UiVec(56f, 56f);
        var drawList = ImGui.GetWindowDrawList();
        drawList.AddRectFilled(origin, origin + iconSize, ImGui.GetColorU32(new Vector4(Accent.X, Accent.Y, Accent.Z, 0.18f)), Ui(9f));
        drawList.AddRect(origin, origin + iconSize, ImGui.GetColorU32(AccentHover), Ui(9f), ImDrawFlags.None, Ui(1.5f));
        DrawCenteredActivityIcon(drawList, icon, origin, iconSize, 1.34f);

        ImGui.SetCursorScreenPos(origin + UiVec(72f, 2f));
        using var uiFontScale8388=UiFonts.PushRelative(1.34f);
        ImGui.TextColored(Hex(0xE2E7EF), title);
        uiFontScale8388.Dispose();
        ImGui.SetCursorScreenPos(origin + UiVec(72f, 30f));
        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + Math.Max(Ui(120f), ImGui.GetContentRegionAvail().X - Ui(4f)));
        using (UiFonts.PushRelative(.90f)) ImGui.TextColored(MutedText, description);
        ImGui.PopTextWrapPos();
        ImGui.SetCursorScreenPos(origin + new Vector2(0f, iconSize.Y));
    }

    private void DrawPictionaryRuleStepper(
        string label,
        string id,
        ref int value,
        int minimum,
        int maximum,
        int step,
        Func<int, string> format)
    {
        var rowOrigin = ImGui.GetCursorScreenPos();
        var rowWidth = ImGui.GetContentRegionAvail().X;
        var rowHeight = Ui(32f);
        var fieldWidth = Math.Min(Ui(190f), rowWidth * 0.48f);
        var fieldX = rowOrigin.X + rowWidth - fieldWidth;
        var buttonWidth = Ui(31f);
        var gap = Ui(4f);
        var valueWidth = fieldWidth - buttonWidth * 2f - gap * 2f;
        var drawList = ImGui.GetWindowDrawList();

        drawList.AddText(rowOrigin + UiVec(0f, 8f), ImGui.GetColorU32(MutedText), label);
        ImGui.SetCursorScreenPos(new Vector2(fieldX, rowOrigin.Y));
        if (ImGui.Button($"-##pictionary{id}Minus", new Vector2(buttonWidth, Ui(30f))))
        {
            value = Math.Max(minimum, value - step);
        }

        ImGui.SameLine(0f, gap);
        var valueMin = ImGui.GetCursorScreenPos();
        ImGui.InvisibleButton($"##pictionary{id}Value", new Vector2(valueWidth, Ui(30f)));
        drawList.AddRectFilled(valueMin, valueMin + new Vector2(valueWidth, Ui(30f)), ImGui.GetColorU32(FrameBg), Ui(7f));
        drawList.AddRect(valueMin, valueMin + new Vector2(valueWidth, Ui(30f)), ImGui.GetColorU32(BorderSubtle), Ui(7f));
        var valueText = format(value);
        var valueSize = ImGui.CalcTextSize(valueText);
        drawList.AddText(valueMin + (new Vector2(valueWidth, Ui(30f)) - valueSize) * 0.5f, ImGui.GetColorU32(Vector4.One), valueText);

        ImGui.SameLine(0f, gap);
        if (ImGui.Button($"+##pictionary{id}Plus", new Vector2(buttonWidth, Ui(30f))))
        {
            value = Math.Min(maximum, value + step);
        }
        ImGui.SetCursorScreenPos(rowOrigin + new Vector2(0f, rowHeight));
    }

    private void DrawPictionaryNextDrawerRule()
    {
        var rowOrigin = ImGui.GetCursorScreenPos();
        var rowWidth = ImGui.GetContentRegionAvail().X;
        var fieldWidth = Math.Min(Ui(190f), rowWidth * 0.48f);
        ImGui.SetCursorScreenPos(rowOrigin + UiVec(0f, 7f));
        ImGui.TextColored(MutedText, "Next drawer");
        ImGui.SetCursorScreenPos(new Vector2(rowOrigin.X + rowWidth - fieldWidth, rowOrigin.Y));
        ImGui.SetNextItemWidth(fieldWidth);
        ImGui.Combo(
            "##pictionaryNextDrawer",
            ref pictionaryCustomNextDrawerIndex,
            PictionaryNextDrawerOptions,
            PictionaryNextDrawerOptions.Length);
        ImGui.SetCursorScreenPos(rowOrigin + UiVec(0f, 32f));
    }

    private static string PictionaryPointsLabel(int points) => points == 1 ? "1 point" : $"{points} points";

    private bool DrawPictionarySetupStartButton(string id, string label, bool enabled)
    {
        var size = new Vector2(ImGui.GetContentRegionAvail().X, Ui(44f));
        using var disabled = UiDisabled.Scope(!enabled);
        using var style = ImRaii.PushStyle(ImGuiStyleVar.FrameRounding, Ui(8f));
        using var colours = ImRaii.PushColor(ImGuiCol.Button, Accent)
            .Push(ImGuiCol.ButtonHovered, AccentHover)
            .Push(ImGuiCol.ButtonActive, AccentActive);
        return ImGui.Button($"{label}##pictionarySetup{id}", size);
    }

    private void DrawPictionarySetupSteps() => DrawArcadeSetupSteps("##pictionarySetupSteps",
        ["Choose a word", "Draw it", "Guess on the TV", "Next drawer"]);

    private void DrawPictionarySetupInfo()
    {
        using var colours = ImRaii.PushColor(ImGuiCol.ChildBg, CardBg).Push(ImGuiCol.Border, BorderSubtle);
        using var style = ImRaii.PushStyle(ImGuiStyleVar.ChildRounding, Ui(9f))
            .Push(ImGuiStyleVar.ChildBorderSize, Ui(1f))
            .Push(ImGuiStyleVar.WindowPadding, UiVec(18f, 13f));
        if (UiFonts.Window(ImGui.GetFontSize(), ImGui.BeginChild("##pictionarySetupInfo", new Vector2(ImGui.GetContentRegionAvail().X, Ui(60f)), true, ImGuiWindowFlags.NoScrollbar)))
        {
            var origin = ImGui.GetCursorScreenPos();
            DrawPictionaryIcon(ImGui.GetWindowDrawList(), FontAwesomeIcon.Tv, origin + UiVec(0f, 2f), 1.15f, new Vector4(0.72f, 0.76f, 0.90f, 1f));
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + Ui(40f));
            ImGui.SetCursorPosY(ImGui.GetCursorPosY() + Ui(4f));
            ImGui.TextColored(MutedText, "The drawing appears on every connected TV. Guess directly on the TV or from this page.");
        }
        ImGui.EndChild();
    }

    private void DrawPictionarySetupBadge(string label, bool primary)
    {
        if (label.Length > 0 && label == label.ToUpperInvariant())
            label = char.ToUpperInvariant(label[0]) + label[1..].ToLowerInvariant().Replace("watch party", "Watch Party").Replace("tv", "TV");
        var textSize = ImGui.CalcTextSize(label);
        var size = textSize + UiVec(28f, 15f);
        var origin = ImGui.GetCursorScreenPos();
        ImGui.Dummy(size);
        var fill = primary ? Accent : AccentActive;
        var drawList = ImGui.GetWindowDrawList();
        drawList.AddRectFilled(origin, origin + size, ImGui.GetColorU32(fill), size.Y * 0.5f);
        drawList.AddText(origin + (size - textSize) * 0.5f, ImGui.GetColorU32(Vector4.One), label);
    }

    private bool CanStartPictionaryFromSetup() =>
        stream.Mode == StreamMode.Hosting && CanLaunchActivity(ActiveActivityKind.Pictionary) &&
        stream.Roster.Length > 0;

    private bool CanRequestPictionaryStart() =>
        stream.Mode == StreamMode.None
            ? CanLaunchActivity(ActiveActivityKind.Pictionary)
            : CanStartPictionaryFromSetup();

    private void RequestPictionaryStart()
    {
        if (!CanRequestPictionaryStart())
            return;

        if (stream.Mode == StreamMode.Hosting)
        {
            StartPictionaryGame();
            return;
        }

        pendingWatchPartyMediaKind = PendingWatchPartyMediaKind.Pictionary;
        OpenPendingWatchPartyCreation();
    }

    private string PictionarySetupStartLabel(string readyLabel)
    {
        if (stream.Mode == StreamMode.None)
        {
            return "Create Watch Party";
        }
        if (stream.Mode != StreamMode.Hosting)
        {
            return "Waiting for host";
        }
        if (!CanLaunchActivity(ActiveActivityKind.Pictionary))
        {
            return "End the active activity first";
        }
        if (!CanStartPictionaryFromSetup())
        {
            return "Needs at least 1 viewer";
        }
        return readyLabel;
    }

    private void ApplyPictionaryStandardRules()
    {
        pictionaryRoundCount = PictionaryStandardRoundCount;
        pictionaryDrawingSeconds = PictionaryStandardDrawingSeconds;
        pictionaryFirstGuessPoints = PictionaryStandardFirstGuessPoints;
        pictionaryOtherGuessPoints = PictionaryStandardOtherGuessPoints;
        pictionaryDrawerPoints = PictionaryStandardDrawerPoints;
        pictionaryFirstGuesserDrawsNext = true;
    }

    private void ApplyPictionaryCustomRules()
    {
        pictionaryRoundCount = Math.Clamp(pictionaryCustomRoundCount, 3, 20);
        pictionaryDrawingSeconds = Math.Clamp(pictionaryCustomDrawingSeconds, 30, 120);
        pictionaryFirstGuessPoints = Math.Clamp(pictionaryCustomFirstGuessPoints, 0, 10);
        pictionaryOtherGuessPoints = Math.Clamp(pictionaryCustomOtherGuessPoints, 0, 10);
        pictionaryDrawerPoints = Math.Clamp(pictionaryCustomDrawerPoints, 0, 10);
        pictionaryFirstGuesserDrawsNext = pictionaryCustomNextDrawerIndex == 0;
    }
}

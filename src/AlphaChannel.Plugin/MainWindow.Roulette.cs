using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private void DrawRoulettePrototypePage()
    {
        lastActivitiesPage = HomePage.Roulette;
        if (!RouletteActive)
        {
            DrawRouletteSetupPage();
            return;
        }

        var available = ImGui.GetContentRegionAvail();
        var footerHeight = Ui(64f);
        var previewWidth = available.X;
        var previewHeight = previewWidth * 9f / 16f;
        var maximumPreviewHeight = MathF.Max(Ui(250f), available.Y - footerHeight - Ui(14f));
        if (previewHeight > maximumPreviewHeight)
        {
            previewHeight = maximumPreviewHeight;
            previewWidth = previewHeight * 16f / 9f;
        }

        var previewSize = new Vector2(previewWidth, previewHeight);
        var previewX = ImGui.GetCursorScreenPos().X + MathF.Max(0f, (available.X - previewWidth) * 0.5f);
        var previewOrigin = new Vector2(previewX, ImGui.GetCursorScreenPos().Y);
        ImGui.SetCursorScreenPos(previewOrigin);

        using (ImRaii.PushStyle(ImGuiStyleVar.FrameRounding, Ui(10f)))
        {
            ImGui.InvisibleButton("##rouletteTvPreview", previewSize);
        }
        var previewHovered = ImGui.IsItemHovered();
        var mouseDown = ImGui.IsMouseDown(ImGuiMouseButton.Left);
        var mousePressed = ImGui.IsMouseClicked(ImGuiMouseButton.Left);
        if ((previewHovered || roulettePageVolumeDragging) && RouletteTvControlsInteractive)
        {
            var uv = (ImGui.GetIO().MousePos - previewOrigin) / previewSize;
            SetRoulettePageHover(previewHovered ? uv : null);
            UpdateRoulettePageVolumePointer(uv, mouseDown, mousePressed && previewHovered);
            if (mousePressed && previewHovered)
                SubmitRouletteTvControlAt(uv);
            if (previewHovered && IsRouletteTvControlAt(uv))
                ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
        }
        else
        {
            SetRoulettePageHover(null);
            UpdateRoulettePageVolumePointer(null, mouseDown, false);
        }

        var drawList = ImGui.GetWindowDrawList();
        drawList.AddRectFilled(previewOrigin, previewOrigin + previewSize, ImGui.GetColorU32(new Vector4(0.02f, 0.025f, 0.04f, 1f)), Ui(10f));
        if (roulettePrototypeActive && screenController.Engine.IsPlayingWhiteboard &&
            screenController.Engine.PreviewTextureHandle != nint.Zero)
        {
            drawList.AddImageRounded(
                new ImTextureID(screenController.Engine.PreviewTextureHandle),
                previewOrigin,
                previewOrigin + previewSize,
                Vector2.Zero,
                Vector2.One,
                uint.MaxValue,
                Ui(10f));
        }
        else
        {
            const string unavailable = "ROULETTE TV PREVIEW IS NOT ACTIVE";
            var textSize = ImGui.CalcTextSize(unavailable);
            drawList.AddText(
                previewOrigin + (previewSize - textSize) * 0.5f,
                ImGui.GetColorU32(MutedText),
                unavailable);
        }
        drawList.AddRect(
            previewOrigin,
            previewOrigin + previewSize,
            ImGui.GetColorU32(new Vector4(Accent.X, Accent.Y, Accent.Z, 0.75f)),
            Ui(10f),
            ImDrawFlags.None,
            Ui(1.5f));

        ImGui.SetCursorScreenPos(new Vector2(ImGui.GetWindowPos().X + ImGui.GetWindowContentRegionMin().X,
            previewOrigin.Y + previewHeight + Ui(14f)));
        var fullWidth = ImGui.GetContentRegionAvail().X;
        var buttonWidth = MathF.Min(Ui(210f), fullWidth * 0.32f);
        var statusWidth = MathF.Max(0f, fullWidth - buttonWidth - Ui(12f));
        using (ImRaii.PushColor(ImGuiCol.ChildBg, CardBg)
                   .Push(ImGuiCol.Border, BorderSubtle))
        using (ImRaii.PushStyle(ImGuiStyleVar.ChildRounding, Ui(9f))
                   .Push(ImGuiStyleVar.ChildBorderSize, Ui(1f))
                   .Push(ImGuiStyleVar.WindowPadding, UiVec(16f, 12f)))
        {
            if (UiFonts.Window(ImGui.GetFontSize(), ImGui.BeginChild("##roulettePrototypeStatus", new Vector2(statusWidth, Ui(48f)), true,
                    ImGuiWindowFlags.NoScrollbar)))
            {
                ImGui.TextColored(
                    RouletteActive ? Good : MutedText,
                    RouletteActive ? roulettePhase switch
                    {
                        RoulettePhase.WaitingForBet => rouletteCroupierMode ? "Croupier: open bets to begin" : "Waiting for the first bet",
                        RoulettePhase.BetsClosed => IsLocalActivityHost ? "No more bets — click the wheel to spin" : "Waiting for the croupier to spin",
                        RoulettePhase.Betting => "Betting is open",
                        RoulettePhase.Spinning => "Wheel spinning",
                        RoulettePhase.Results => rouletteCroupierMode ? "Round settled — croupier opens the next round" : "Showing round result",
                        _ => "Roulette inactive",
                    } : "Roulette inactive");
                ImGui.SameLine(0f, Ui(10f));
                ImGui.TextColored(MutedText, string.IsNullOrWhiteSpace(rouletteMusicTitle)
                    ? "Loading Jazz from Incompetech..."
                    : $"Music: {rouletteMusicTitle} — Kevin MacLeod (incompetech.com), CC BY 4.0");
            }
            ImGui.EndChild();
        }

        ImGui.SameLine(0f, Ui(12f));
        if (IsLocalActivityHost && RouletteActive &&
            GameLayoutButton("End Roulette", FontAwesomeIcon.Stop, buttonWidth, true, height: 48f))
        {
            StopRouletteGame();
        }

        DrawRouletteTransferOverlay();
    }

    private void DrawRouletteSetupPage()
    {
        using var spacing = ImRaii.PushStyle(ImGuiStyleVar.ItemSpacing, UiVec(10f, 12f));
        DrawRouletteSetupHero();

        var available = ImGui.GetContentRegionAvail().X;
        var gap = Ui(12f);
        var stacked = available < Ui(760f);
        var standardWidth = stacked ? available : (available - gap) * 0.40f;
        var customWidth = stacked ? available : available - gap - standardWidth;
        DrawRouletteStandardCard(standardWidth);
        if (!stacked) ImGui.SameLine(0f, gap);
        DrawRouletteCustomCard(customWidth);
        DrawRouletteSetupInfo();
    }

    private void DrawRouletteSetupHero() => DrawArcadeReviewHero("roulette", "Roulette", "Place your bets together", "Choose standard play or customise the table before spawning Roulette on your TV.", "Automatic rounds", false, ActivityPreviewKind.Roulette);


    private void DrawRouletteStandardCard(float width)
    {
        const float designHeight = 370f;
        DrawQuizCardBegin("##rouletteStandard", width, Ui(designHeight));
        var origin = ImGui.GetWindowPos();
        DrawPictionarySetupBadge("Standard play", true);
        ImGui.Dummy(UiVec(0f, 8f));
        DrawPictionarySetupCardHeading(FontAwesomeIcon.Circle, "Standard roulette",
            "Start a balanced table with automatic rounds.");
        ImGui.Dummy(UiVec(0f, 18f));
        DrawActivityFeature("15 second bet timer");
        DrawActivityFeature("200 starting chips");
        DrawActivityFeature("Refill chips when empty");
        ImGui.SetCursorScreenPos(new Vector2(origin.X + Ui(18f), origin.Y + Ui(designHeight - 54f)));
        DrawActivityStartButtons("rouletteStandard", () => RequestRouletteStart(true),
            () => RequestRouletteStart(true, true), CanRequestRouletteStart());
        ImGui.EndChild();
    }

    private void DrawRouletteCustomCard(float width)
    {
        const float designHeight = 440f;
        DrawQuizCardBegin("##rouletteCustom", width, Ui(designHeight), accentBorder: true);
        var origin = ImGui.GetWindowPos();
        DrawPictionarySetupBadge("Custom play", false);
        ImGui.Dummy(UiVec(0f, 8f));
        DrawPictionarySetupCardHeading(FontAwesomeIcon.Cog, "Custom roulette",
            "Choose the pace, starting balance, and refill rule.");
        ImGui.Dummy(UiVec(0f, 12f));

        var formOrigin = ImGui.GetCursorScreenPos();
        var formWidth = ImGui.GetContentRegionAvail().X;
        var columnGap = Ui(18f);
        var columnWidth = (formWidth - columnGap) * 0.5f;
        using (UiDisabled.Scope(stream.Mode == StreamMode.Viewing))
        using (ImRaii.PushStyle(ImGuiStyleVar.FrameRounding, Ui(7f)))
        {
            using (UiDisabled.Scope(rouletteCustomCroupierMode))
                DrawQuizGridStepper(formOrigin, columnWidth, "Bet timer", "rouletteTimer",
                    ref rouletteCustomBettingSeconds, 5, 60, 5, value => $"{value} seconds");
            DrawQuizGridStepper(new Vector2(formOrigin.X + columnWidth + columnGap, formOrigin.Y), columnWidth,
                "Starting chips", "rouletteChips", ref rouletteCustomStartingChips, 25, 5000, 25,
                value => value.ToString());

            ImGui.SetCursorScreenPos(formOrigin + UiVec(0f, 76f));
            ImGui.Checkbox("Refill chips when a player reaches zero##rouletteRefill", ref rouletteCustomRefillEnabled);
            ImGui.SetCursorScreenPos(formOrigin + UiVec(0f, 106f));
            ImGui.TextColored(MutedText, rouletteCustomRefillEnabled
                ? $"Players refill to {rouletteCustomStartingChips} chips after losing their full balance."
                : "Players remain at zero until Roulette is restarted.");
            ImGui.SetCursorScreenPos(formOrigin + UiVec(0f, 136f));
            ImGui.Checkbox("Croupier Mode##rouletteCroupier", ref rouletteCustomCroupierMode);
            if (ImGui.IsItemHovered())
                UiFonts.SetTooltip("The host runs the table and cannot bet. Open and close bets manually, then click the wheel to spin. No automatic betting timers.");
            using (ImRaii.PushColor(ImGuiCol.Text, MutedText))
                ImGui.TextWrapped("The host runs the table without betting. No automatic bet timers.");
        }

        ImGui.SetCursorScreenPos(new Vector2(origin.X + Ui(18f), origin.Y + Ui(designHeight - 54f)));
        DrawActivityStartButtons("rouletteCustom", () => RequestRouletteStart(false),
            () => RequestRouletteStart(false, true), CanRequestRouletteStart());
        ImGui.EndChild();
    }

    private void DrawRouletteSetupInfo()
    {
        using var colours = ImRaii.PushColor(ImGuiCol.ChildBg, CardBg).Push(ImGuiCol.Border, BorderSubtle);
        using var style = ImRaii.PushStyle(ImGuiStyleVar.ChildRounding, Ui(9f))
            .Push(ImGuiStyleVar.ChildBorderSize, Ui(1f)).Push(ImGuiStyleVar.WindowPadding, UiVec(18f, 13f));
        if (UiFonts.Window(ImGui.GetFontSize(), ImGui.BeginChild("##rouletteSetupInfo", new Vector2(-1f, Ui(60f)), true, ImGuiWindowFlags.NoScrollbar)))
        {
            var origin = ImGui.GetCursorScreenPos();
            UiFonts.DrawIcon(ImGui.GetWindowDrawList(), ImGui.GetFontSize() * 1.2f, origin + UiVec(0f, 2f), ImGui.GetColorU32(new Vector4(0.72f, 0.76f, 0.90f, 1f)), FontAwesomeIcon.Tv.ToIconString());
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + Ui(40f));
            ImGui.SetCursorPosY(ImGui.GetCursorPosY() + Ui(4f));
            ImGui.TextColored(MutedText, rouletteCustomCroupierMode ? "Croupier Mode: the host runs each round; players place bets when the table is open." : "The first bet starts the timer. The wheel then spins and settles the round automatically.");
        }
        ImGui.EndChild();
    }

    private bool CanRequestRouletteStart() =>
        stream.Mode != StreamMode.Viewing && ActiveActivity == ActiveActivityKind.None;

    private void RequestRouletteStart(bool standardPlay, bool inParty = false)
    {
        if (!CanRequestRouletteStart()) return;

        pendingRouletteBettingSeconds = standardPlay
            ? RouletteDefaultBettingSeconds
            : Math.Clamp(rouletteCustomBettingSeconds, 5, 60);
        pendingRouletteStartingChips = standardPlay
            ? RouletteDefaultStartingChips
            : Math.Clamp(rouletteCustomStartingChips, 25, 5000);
        pendingRouletteRefillEnabled = standardPlay || rouletteCustomRefillEnabled;
        pendingRouletteCroupierMode = !standardPlay && rouletteCustomCroupierMode;

        if (!inParty || stream.Mode == StreamMode.Hosting)
        {
            StartRouletteGame();
            return;
        }

        pendingWatchPartyMediaKind = PendingWatchPartyMediaKind.Roulette;
        OpenPendingWatchPartyCreation();
    }
}

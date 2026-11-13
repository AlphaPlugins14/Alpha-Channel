using AlphaChannel.Plugin.Video;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    // Set to false to restore the original single-screen race track.
    private const bool ChocoRaceScrollingTrackEnabled = true;
    private const int ChocoRaceTrackTravel = 896;
    private const int ChocoRaceLongTrackTravel = (int)(ChocoRaceTrackTravel * 2 * 1.3d);

    private bool racePreviewActive;
    private readonly List<WhiteboardRenderer> retiredChocoRacePageRenderers = [];

    private void OpenRacePreviewPage()
    {
        if (!CanLaunchActivity(ActiveActivityKind.ChocoRaceBets))
            return;
        currentPage = HomePage.RacePreview;
        lastActivitiesPage = HomePage.RacePreview;
    }

    private void StopRacePreview()
    {
        if (ChocoRaceActive) StopChocoRace();
        else ResetChocoRaceLocal();
    }

    internal void UpdateRacePreview()
    {
        if (stream.Mode == StreamMode.None)
            stream.IncomingChocoRaceCommands.Clear();
        while (stream.IncomingChocoRaceCommands.TryDequeue(out var incoming))
        {
            ReceiveArcadeServerCommand("choco-race", incoming.UserId, incoming.Command);
        }
        UpdateActivityRelay("choco-race");
        if (!ChocoRaceActive) return;
        // Solo play owns the race too; no party connection is required to keep
        // the session alive or advance betting, racing and results.
        if (IsLocalActivityHost)
        {
            if (!whiteboardActive || !string.Equals(whiteboardSessionId, chocoRaceWhiteboardSessionId, StringComparison.Ordinal))
            {
                StopChocoRace();
                return;
            }
            var rosterChanged = AddChocoRaceRosterPlayers();
            var countdownShortened = TryShortenChocoRaceCountdown();
            if (rosterChanged || countdownShortened) { chocoRaceRevision++; SendChocoRaceState(); }
            var hostNow = DateTime.UtcNow;
            if (chocoRacePhase == ChocoRacePhase.Betting && hostNow >= chocoRacePhaseEndsUtc) BeginChocoRace();
            else if (chocoRacePhase == ChocoRacePhase.Racing && hostNow >= chocoRacePhaseEndsUtc) SettleChocoRace();
            else if (chocoRacePhase == ChocoRacePhase.Results && hostNow >= chocoRacePhaseEndsUtc) BeginNextChocoRaceRound();
        }

        UpdateChocoRaceAudio();

        var now = DateTime.UtcNow;
        RetryChocoRacePendingBet(now);
        if (!ChocoRaceBetsOpen && ChocoRaceDraftTotal > 0) ResetChocoRaceDraft();
        if (!ChocoRaceBetsOpen && chocoRaceSelectedLane >= 0)
            chocoRaceSelectedLane = -1;
        var showPage = IsOpen && currentPage == HomePage.RacePreview;
        var showTv = ShouldRenderArcadeTv;
        UpdateChocoRaceIntroVisibility(showPage, showTv, now);
        if (!chocoRaceDirty && (now - chocoRaceLastFrameUtc).TotalMilliseconds < 50d) return;
        chocoRaceLastFrameUtc = now;
        if (!showPage && !showTv) return;
        var frameStarted = System.Diagnostics.Stopwatch.GetTimestamp();
        var pixels = BuildRacePreviewPixels(ChocoRaceNowUtc);
        PlaybackRenderDiagnostics.RecordArcadeRender("Choco RaceBets",
            System.Diagnostics.Stopwatch.GetElapsedTime(frameStarted).TotalMilliseconds);
        if (showPage)
        {
            var pagePixels = (byte[])pixels.Clone();
            ApplyChocoRaceIntroPixels(pagePixels, now, chocoRacePageIntroStartedUtc);
            if (chocoRacePageRenderer is null && !screenController.Engine.TryInitializeGraphics()) return;
            chocoRacePageRenderer ??= DxHandler.RunOnRenderThreadAndWait(
                "AlphaChannel.ChocoRacePage.Create",
                () => new WhiteboardRenderer("AlphaChannel.ChocoRacePage"),
                TimeSpan.FromSeconds(30));
            chocoRacePageRenderer.Upload(pagePixels);
        }
        if (showTv)
        {
            ApplyChocoRaceIntroPixels(pixels, now, chocoRaceTvIntroStartedUtc);
            screenController.Engine.UpdateWhiteboard(pixels);
        }
        chocoRaceDirty = false;
        whiteboardTvDirty = false;
    }

    private void DisposeChocoRacePageRenderer(bool flush = false)
    {
        var renderer = chocoRacePageRenderer;
        chocoRacePageRenderer = null;
        if (renderer is not null)
        {
            lock (retiredChocoRacePageRenderers)
                retiredChocoRacePageRenderers.Add(renderer);
            // A close button can run inside ImGui's draw callback. Defer release
            // until the next Present, after that frame has consumed PreviewHandle.
            DxHandler.RunOnRenderThread(
                "AlphaChannel.ChocoRacePage.Dispose",
                DrainRetiredChocoRacePageRenderers, allowAfterDeviceLoss: true);
        }
        if (flush)
            DxHandler.QueueShutdownWork(DrainRetiredChocoRacePageRenderers);
    }

    private void DrainRetiredChocoRacePageRenderers()
    {
        WhiteboardRenderer[] retired;
        lock (retiredChocoRacePageRenderers)
        {
            retired = retiredChocoRacePageRenderers.ToArray();
            retiredChocoRacePageRenderers.Clear();
        }
        foreach (var renderer in retired)
            renderer.Dispose();
    }

    private void DrawRacePreviewPage()
    {
        lastActivitiesPage = HomePage.RacePreview;
        if (ChocoRaceActive)
        {
            var available = ImGui.GetContentRegionAvail().X;
            var previewSize = new Vector2(available, available * 9f / 16f);
            ImGui.Image(new ImTextureID(chocoRacePageRenderer?.PreviewHandle ?? nint.Zero), previewSize);
            var imageHovered = ImGui.IsItemHovered();
            var mouseDown = ImGui.IsMouseDown(ImGuiMouseButton.Left);
            var mousePressed = ImGui.IsMouseClicked(ImGuiMouseButton.Left);
            if ((imageHovered || chocoRacePageVolumeDragging) &&
                !ChocoRacePageIntroBlocksInput)
            {
                var uv = (ImGui.GetIO().MousePos - ImGui.GetItemRectMin()) / previewSize;
                SetChocoRacePageHover(imageHovered ? uv : null);
                UpdateChocoRacePageVolumePointer(uv, mouseDown, mousePressed && imageHovered);
                if (mousePressed && imageHovered) SubmitChocoRacePageControlAt(uv);
                if (imageHovered && IsChocoRacePageControlAt(uv)) ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
            }
            else
            {
                SetChocoRacePageHover(null);
                UpdateChocoRacePageVolumePointer(null, mouseDown, false);
            }
            ImGui.Spacing();
            var status = ChocoRaceIntroActive(DateTime.UtcNow, chocoRacePageIntroStartedUtc)
                ? "Opening the arcade..." : chocoRacePhase switch
            {
                ChocoRacePhase.WaitingForBet => "Waiting for the first bet",
                ChocoRacePhase.Betting => $"Bets close in {ChocoRaceSecondsLeft} seconds",
                ChocoRacePhase.Racing => "Chocos are racing",
                ChocoRacePhase.Results => "Race results",
                _ => "Race inactive",
            };
            ImGui.TextColored(MutedText, status);
            var ownBets = chocoRaceBets.Where(bet => bet.PlayerId == LocalChocoRaceUserId)
                .Select(bet => $"{bet.Amount} on {chocoRaceRacers[bet.Lane].Name}").ToArray();
            if (ownBets.Length > 0)
                ImGui.TextWrapped("Your bets: " + string.Join("  •  ", ownBets));
            if (stream.Mode == StreamMode.Hosting && ImGui.Button("End Choco RaceBets", UiVec(190f, 38f)))
            {
                StopRacePreview();
                currentPage = HomePage.Arcade;
            }
        }
        else
        {
            DrawChocoRaceSetupPage();
        }
    }

    private byte[] BuildRacePreviewPixels(DateTime now)
    {
        const int width = WhiteboardRenderer.Width, height = WhiteboardRenderer.Height;
        const uint bg = 0x0D1018, purpleBg = 0x21162F, purple = 0x8959ED;
        const uint white = 0xF5F3FB, muted = 0xB9A9CF, gold = 0xF6BC4E;
        const uint track = 0x114321, trackAlt = 0x164B28, cream = 0xD7CC9A;
        var pixels = new byte[width * height * 4];
        var hoverPulse = ChocoRaceHoverPulse(now);
        FillPictionaryPixels(pixels, width, height, 0, 0, width, height, bg);
        FillQuizTvPanel(pixels, width, height, 11, 10, 1258, 108, purpleBg, purple);
        FillPictionaryPixels(pixels, width, height, 20, 109, 1240, 3, 0x55326F);
        for (var row = 0; row < 3; row++)
        for (var column = 0; column < 3; column++)
        {
            if ((row + column) % 2 != 0) continue;
            FillPictionaryPixels(pixels, width, height, 22 + column * 12, 19 + row * 12, 12, 12, 0x6E3DA4);
            FillPictionaryPixels(pixels, width, height, 1222 + column * 12, 19 + row * 12, 12, 12, 0x6E3DA4);
        }
        DrawRaceDuckLarge(pixels, width, height, 308, 97);
        DrawRaceDuckLarge(pixels, width, height, 972, 97);
        DrawPictionaryPixelTextCentered(pixels, width, height, 640, 25, "CHOCO RACEBETS", 6, gold);
        DrawPictionaryPixelTextCentered(pixels, width, height, 640, 83,
            "SIX CHOCOBOS. ONE GLORIOUS WINNER.", 2, gold);
        DrawPictionaryPixelText(pixels, width, height, 1091, 47, $"RACE {chocoRaceRound:00}", 2, muted);
        DrawChocoRaceScoreboardButton(pixels);
        DrawChocoRaceHelpButton(pixels);
        DrawChocoRaceSettingsButton(pixels);
        if (chocoRaceScoreboardOpen)
        {
            DrawChocoRaceScoreboardPixels(pixels, now);
            DrawChocoRaceSettingsOverlay(pixels);
            DrawChocoRaceHelpOverlay(pixels);
            return pixels;
        }

        FillQuizTvPanel(pixels, width, height, 17, 124, 1246, 322, track, purple);
        FillPictionaryPixels(pixels, width, height, 21, 128, 1238, 3, 0x306336);
        FillPictionaryPixels(pixels, width, height, 21, 440, 1238, 3, 0x306336);
        var laneColours = new uint[] { 0xF5CB48, 0x56BFEF, 0xF27B99, 0xF5F4EE, 0xB478E9, 0x474A55 };
        var wingColours = new uint[] { 0xF9DE79, 0xA9E6FF, 0xFFACBB, 0xFFFFFF, 0xD4A8FA, 0x737887 };
        var badgeColours = new uint[] { 0xC93C50, 0x397CD3, 0x20965B, 0xF1C343, 0x9252D5, 0x6C717B };
        var runningFrame = chocoRacePhase == ChocoRacePhase.Racing
            ? (int)(now.Ticks / TimeSpan.TicksPerMillisecond / 160 % 2) : 0;
        // Follow the leading choco after it reaches the middle of the screen. The
        // second half of the course, including the finish, starts off-screen.
        var courseTravel = ChocoRaceScrollingTrackEnabled ? ChocoRaceLongTrackTravel : ChocoRaceTrackTravel;
        var cameraX = ChocoRaceCameraX(now);
        var (gateHopLane, gateHopOffset) = ChocoRaceGateHop(now);
        for (var index = 0; index < 6; index++)
        {
            var laneTop = 143 + index * 48;
            var hovered = ChocoRaceBetsOpen && chocoRaceHoveredLane == index;
            var selected = ChocoRaceBetsOpen && chocoRaceSelectedLane == index;
            FillPictionaryPixels(pixels, width, height, 28, laneTop, 1218, 47,
                hovered ? 0x205B31u : selected ? 0x1A512Cu :
                    index % 2 == 0 ? track : trackAlt);
            for (var repeat = 0; repeat < (ChocoRaceScrollingTrackEnabled ? 3 : 1); repeat++)
            for (var tuft = 0; tuft < 62; tuft++)
            {
                var tx = 93 + (tuft * 71 + index * 39) % 994 + repeat * 994 - cameraX;
                if (ChocoRaceScrollingTrackEnabled && (tx < 110 || tx > 1073)) continue;
                var ty = laneTop + 8 + (tuft * 13 + index * 7) % 27;
                FillPictionaryPixels(pixels, width, height, tx, ty, 3, 3, 0x33783A);
                FillPictionaryPixels(pixels, width, height, tx + 4, ty - 3, 2, 4, 0x3D853C);
                FillPictionaryPixels(pixels, width, height, tx + 8, ty + 1, 2, 2, 0x2D6A35);
            }
            FillPictionaryPixels(pixels, width, height, 28, laneTop + 43, 1218, 2, 0x759264);
            FillPictionaryPixels(pixels, width, height, 28, laneTop + 45, 1218, 2, cream);
            var racer = chocoRaceRacers[index];
            var colourIndex = Math.Clamp(racer.ColourIndex, 0, laneColours.Length - 1);
            if (hovered)
                FillQuizTvPanel(pixels, width, height, 32, laneTop, 49, 46, 0x533A25, gold);
            FillQuizTvPanel(pixels, width, height, 35, laneTop + 3, 43, 40,
                badgeColours[colourIndex],
                selected || hovered ||
                chocoRacePhase == ChocoRacePhase.Results && chocoRaceWinnerLane == index ? gold : cream);
            DrawPictionaryPixelTextCentered(pixels, width, height, 56, laneTop + 12,
                (index + 1).ToString(), 3, white);
            var duckX = 152 + (int)Math.Round(courseTravel * ChocoRaceProgress(index, now)) - cameraX;
            DrawChocoRaceObstaclePixels(pixels, index, laneTop, courseTravel, cameraX, duckX, now);
            if (chocoRacePhase == ChocoRacePhase.Racing)
            {
                FillPictionaryPixels(pixels, width, height, duckX - 53, laneTop + 24, 14, 2, cream);
                FillPictionaryPixels(pixels, width, height, duckX - 43, laneTop + 30, 10, 2, cream);
                FillPictionaryPixels(pixels, width, height, duckX - 36, laneTop + 20, 7, 2, cream);
            }
            if (hovered && duckX is > 104 and < 1110)
                RaceCircle(pixels, width, height, duckX - 4, laneTop + 27,
                    24 + hoverPulse, gold, false, 2);
            var jumpOffset = ChocoRaceJumpOffset(index, now) +
                (gateHopLane == index ? gateHopOffset : 0);
            if (jumpOffset > 0)
            {
                FillPictionaryPixels(pixels, width, height, duckX - 13, laneTop + 39,
                    24, 2, 0x749669);
                if (gateHopLane == index && jumpOffset >= 5)
                {
                    RaceLine(pixels, width, height, duckX - 34, laneTop + 16,
                        duckX - 29, laneTop + 11, gold, 2);
                    RaceLine(pixels, width, height, duckX + 38, laneTop + 10,
                        duckX + 43, laneTop + 5, gold, 2);
                }
            }
            var duckFrame = chocoRacePhase == ChocoRacePhase.Racing ? runningFrame :
                gateHopLane == index ? (int)(now.Ticks / TimeSpan.TicksPerMillisecond / 120 % 2) : 0;
            DrawRaceDuck(pixels, width, height, duckX, laneTop + 42 - runningFrame - jumpOffset,
                laneColours[colourIndex], wingColours[colourIndex],
                colourIndex == 5 ? white : 0x232331, duckFrame);
            DrawChocoRaceMeteorPixels(pixels, index, laneTop, duckX, now);
            if (chocoRacePhase == ChocoRacePhase.Results && chocoRaceWinnerLane == index)
                DrawPictionaryPixelText(pixels, width, height, 951, laneTop + 16, "WIN", 2, gold);
        }
        // The start scrolls away, then the checkered finish enters from the right.
        var startX = 103 - cameraX;
        if (startX >= 103)
        {
            for (var row = 0; row < 36; row++)
                FillPictionaryPixels(pixels, width, height, startX, 143 + row * 8,
                    5, 8, row % 2 == 0 ? cream : track);
            DrawPictionaryPixelText(pixels, width, height, 87, 131, "START", 1, cream);
        }
        var finishX = 1083 + (ChocoRaceScrollingTrackEnabled ?
            courseTravel - ChocoRaceTrackTravel + 1 - cameraX : 0);
        if (finishX < 1120)
        {
            for (var row = 0; row < 36; row++)
            for (var column = 0; column < 4; column++)
            {
                var stripeX = finishX + column * 9;
                if (stripeX >= 1120) continue;
                FillPictionaryPixels(pixels, width, height, stripeX, 143 + row * 8,
                    Math.Min(9, 1120 - stripeX), 8, (row + column) % 2 == 0 ? white : 0x17171B);
            }
            FillQuizTvPanel(pixels, width, height, finishX - 22, 128, 84, 27, purpleBg, gold);
            DrawPictionaryPixelTextCentered(pixels, width, height, finishX + 20, 134, "FINISH", 2, gold);
        }
        // Old cabinet-style Good Luck sign remains fixed beside the course.
        FillPictionaryPixels(pixels, width, height, 1165, 151, 4, 33, purple);
        FillPictionaryPixels(pixels, width, height, 1234, 151, 4, 33, purple);
        RaceCircle(pixels, width, height, 1167, 151, 5, 0xB47FE0);
        RaceCircle(pixels, width, height, 1236, 151, 5, 0xB47FE0);
        FillQuizTvPanel(pixels, width, height, 1153, 176, 94, 204, purpleBg, purple);
        DrawPictionaryPixelTextCentered(pixels, width, height, 1200, 199, "GOOD", 2, gold);
        DrawPictionaryPixelTextCentered(pixels, width, height, 1200, 225, "LUCK!", 2, gold);
        DrawRaceDuck(pixels, width, height, 1200, 307, 0xF5CB48, 0xF9DE79, 0x232331);
        RaceLine(pixels, width, height, 1200, 334, 1200, 351, gold, 4);
        RaceLine(pixels, width, height, 1189, 342, 1211, 342, gold, 4);
        FillPictionaryPixels(pixels, width, height, 1161, 371, 78, 2, purple);
        DrawChocoRaceConditionsPixels(pixels);

        // The host publishes the same racers, odds, bets and phase clock to every player.
        FillQuizTvPanel(pixels, width, height, 17, 457, 995, 79, 0x211923, gold);
        for (var index = 0; index < 6; index++)
        {
            var center = 100 + index * 165;
            if (ChocoRaceBetsOpen && (chocoRaceHoveredLane == index || chocoRaceSelectedLane == index))
                FillQuizTvPanel(pixels, width, height, 19 + index * 165, 459, 161, 75,
                    chocoRaceHoveredLane == index ? 0x503A2Bu : 0x37273Fu,
                    chocoRaceHoveredLane == index ? gold : purple);
            if (index > 0) FillPictionaryPixels(pixels, width, height, 17 + index * 165, 468, 2, 56, gold);
            DrawPictionaryPixelTextCentered(pixels, width, height, center, 465,
                (index + 1).ToString(), 2, laneColours[chocoRaceRacers[index].ColourIndex]);
            DrawPictionaryPixelTextCentered(pixels, width, height, center, 487,
                chocoRaceRacers[index].Name.ToUpperInvariant(), 2, white);
            DrawPictionaryPixelTextCentered(pixels, width, height, center, 505,
                $"{chocoRaceRacers[index].Odds:0.0}:1", 3, gold);
            DrawChocoRaceWeatherIcon(pixels, index);
            DrawChocoRaceBetStack(pixels, index, now);
            if (chocoRaceDraftByLane[index] > 0 && chocoRacePhase != ChocoRacePhase.Results)
            {
                var pendingX = 164 + index * 165;
                RaceCircle(pixels, width, height, pendingX, 472, 12, 0xF6BC4E);
                RaceCircle(pixels, width, height, pendingX, 472, 9, 0x604286);
                var pendingAmount = chocoRaceDraftByLane[index];
                DrawPictionaryPixelTextCentered(pixels, width, height, pendingX, 468,
                    pendingAmount >= 1000 ? $"{pendingAmount / 1000d:0.#}K" : pendingAmount.ToString(),
                    1, 0xFFF5D8);
            }
        }
        FillQuizTvPanel(pixels, width, height, 1021, 457, 242, 79, 0x211923, gold);
        var clockHeading = chocoRacePhase switch
        {
            ChocoRacePhase.WaitingForBet => "FIRST BET STARTS",
            ChocoRacePhase.Betting => "RACE STARTS IN",
            ChocoRacePhase.Racing => "RACE IN PROGRESS",
            _ => "RACE FINISHED",
        };
        DrawPictionaryPixelTextCentered(pixels, width, height, 1142, 467, clockHeading, 2, gold);
        DrawPictionaryPixelTextCentered(pixels, width, height, 1142, 486,
            chocoRacePhase == ChocoRacePhase.WaitingForBet ? "--:--" :
            chocoRacePhase == ChocoRacePhase.Results ? "FINAL" : $"00:{ChocoRaceSecondsLeft:00}", 6, gold);

        var localId = LocalChocoRaceUserId;
        var localChips = chocoRacePlayers.TryGetValue(localId, out var localPlayer)
            ? localPlayer.Chips : ChocoRaceStartingChips;
        var localStake = chocoRaceBets.Where(bet => bet.PlayerId == localId).Sum(bet => bet.Amount);
        var localBets = chocoRaceBets.Where(bet => bet.PlayerId == localId).ToArray();
        var shownBets = ChocoRaceDraftTotal > 0
            ? Enumerable.Range(0, 6).Where(lane => chocoRaceDraftByLane[lane] > 0)
                .Select(lane => new ChocoRaceBet(localId, lane, chocoRaceDraftByLane[lane])).ToArray()
            : localBets;
        var maxReturn = shownBets.Length == 0 ? 0 : shownBets.Max(bet =>
            ChocoRaceWinningReturn(bet.Amount, chocoRaceRacers[bet.Lane].Odds));
        var localResult = chocoRacePayouts.FirstOrDefault(payout => payout.PlayerId == localId);
        FillQuizTvPanel(pixels, width, height, 17, 545, 240, 103, purpleBg, purple);
        DrawPictionaryPixelText(pixels, width, height, 33, 554, "YOUR CHIPS", 2, muted);
        FillQuizTvPanel(pixels, width, height, 30, 581, 213, 56, 0x11131D, 0x5E4A77);
        RaceCircle(pixels, width, height, 61, 609, 17, gold);
        RaceCircle(pixels, width, height, 61, 609, 11, 0xA96125);
        DrawPictionaryPixelTextCentered(pixels, width, height, 61, 602, "C", 2, gold);
        DrawPictionaryPixelText(pixels, width, height, 96, 589, localChips.ToString(), 5, gold);

        FillQuizTvPanel(pixels, width, height, 265, 545, 494, 103, purpleBg, purple);
        DrawPictionaryPixelText(pixels, width, height, 281, 554, "CHOOSE CHIP", 2, muted);
        if (now < chocoRaceNotEnoughChipsUntilUtc)
            DrawPictionaryPixelTextCentered(pixels, width, height, 600, 554,
                "NOT ENOUGH CHIPS", 2, 0xFF6674);
        var chipColours = new uint[] { 0x269454, 0x357AC9, 0xC7444E, 0x8652C9, 0xC69330 };
        var chips = new[] { "10", "50", "100", "500", "1K" };
        for (var index = 0; index < chips.Length; index++)
        {
            var cx = 309 + index * 91;
            var hovered = chocoRaceHoveredControlTarget == 100 + index;
            var clicked = ChocoRaceClickFlash(100 + index, now);
            if (hovered || clicked)
                RaceCircle(pixels, width, height, cx, 610,
                    32 + (hovered ? hoverPulse : 2), clicked ? white : gold, false, 3);
            RaceCircle(pixels, width, height, cx, 610, 29,
                chocoRaceSelectedChip == ChocoRaceChipValues[index] ? gold : 0xF2E9D4);
            RaceCircle(pixels, width, height, cx, 610, 25, chipColours[index]);
            RaceCircle(pixels, width, height, cx, 610, 19, 0xF2E9D4, false, 2);
            FillPictionaryPixels(pixels, width, height, cx - 3, 583, 6, 5, 0xF2E9D4);
            FillPictionaryPixels(pixels, width, height, cx - 3, 632, 6, 5, 0xF2E9D4);
            FillPictionaryPixels(pixels, width, height, cx - 27, 607, 5, 6, 0xF2E9D4);
            FillPictionaryPixels(pixels, width, height, cx + 22, 607, 5, 6, 0xF2E9D4);
            DrawPictionaryPixelTextCentered(pixels, width, height, cx, 603, chips[index], 2, white);
        }

        FillQuizTvPanel(pixels, width, height, 767, 545, 496, 103, purpleBg, purple);
        DrawPictionaryPixelText(pixels, width, height, 782, 554, "BET SLIP", 2, muted);
        FillQuizTvPanel(pixels, width, height, 779, 581, 195, 55, 0x11131D, 0x5E4A77);
        var slipText = chocoRacePhase == ChocoRacePhase.Results
            ? chocoRaceRefilledPlayerIds.Contains(localId) ? "RESET TO 1000" :
                localResult is { } result
                    ? result.Return > result.Stake ? $"WON {result.Return - result.Stake}" :
                        result.Return == result.Stake ? "BREAK EVEN" : $"LOST {result.Stake - result.Return}"
                    : "NO BET THIS RACE"
            : shownBets.Length > 1 ? $"{shownBets.Sum(bet => bet.Amount)} TOTAL" :
                shownBets.Length == 1 ? $"{shownBets[0].Amount} ON #{shownBets[0].Lane + 1}" :
                "NO SELECTION";
        DrawPictionaryPixelTextCentered(pixels, width, height, 876, 593, slipText, 2, muted);
        if (ChocoRaceDraftTotal > 0)
            DrawPictionaryPixelTextCentered(pixels, width, height, 876, 617,
                chocoRacePendingBetId is null ? "(PENDING)" : "(SENDING)", 1, gold);
        else if (localStake > 0 && chocoRacePhase != ChocoRacePhase.Results)
            DrawPictionaryPixelTextCentered(pixels, width, height, 876, 617, "BET LOCKED", 1, gold);
        DrawPictionaryPixelText(pixels, width, height, 987, 558,
            $"TOTAL {(ChocoRaceDraftTotal > 0 ? ChocoRaceDraftTotal : localStake)}", 2, muted);
        DrawPictionaryPixelText(pixels, width, height, 987, 581,
            $"{(chocoRacePhase == ChocoRacePhase.Results ? "PAYOUT" : "MAX PAY")} {(chocoRacePhase == ChocoRacePhase.Results ? localResult?.Return ?? 0 : maxReturn)}", 2, muted);
        var canClear = ChocoRaceCanEditDraft && ChocoRaceDraftTotal > 0;
        FillQuizTvPanel(pixels, width, height, 1160, 549, 90, 29,
            canClear && chocoRaceHoveredControlTarget == 220 ? 0x604286u : 0x211923u,
            canClear ? gold : 0x5E4A77u);
        DrawPictionaryPixelTextCentered(pixels, width, height, 1205, 557,
            "CLEAR", 2, canClear ? white : muted);
        var canPlace = ChocoRaceCanPlaceDraft && localChips >= ChocoRaceDraftTotal;
        var betHovered = canPlace && chocoRaceHoveredControlTarget == 200;
        var betClicked = ChocoRaceClickFlash(200, now);
        var urgent = canPlace && chocoRacePhase == ChocoRacePhase.Betting &&
            (chocoRacePhaseEndsUtc - now).TotalSeconds is > 0 and < 5 &&
            (int)(now.TimeOfDay.TotalMilliseconds / 300) % 2 == 0;
        FillQuizTvPanel(pixels, width, height, 985, 607, 265, 30,
            betHovered || betClicked || urgent ? 0x8959EDu : canPlace ? 0x603BA0u : 0x303744u,
            betHovered || betClicked || urgent ? gold : canPlace ? purple : 0x656B79u);
        if (betHovered || betClicked)
            FillPictionaryPixels(pixels, width, height, 991, 611, 3 + hoverPulse, 22, gold);
        DrawPictionaryPixelTextCentered(pixels, width, height, 1117, 614,
            betClicked || chocoRacePendingBetId is not null ? "BET SENT" : "PLACE BET", 2,
            canPlace || betClicked ? white : muted);

        FillQuizTvPanel(pixels, width, height, 17, 658, 1246, 46, 0x211923, gold);
        DrawPictionaryPixelTextCentered(pixels, width, height, 640, 665, ChocoRaceTicker(), 3, gold);
        for (var arrow = 0; arrow < 3; arrow++)
        {
            var left = 350 + arrow * 22;
            RaceLine(pixels, width, height, left, 671, left + 10, 679, gold, 3);
            RaceLine(pixels, width, height, left + 10, 679, left, 687, gold, 3);
            var right = 930 - arrow * 22;
            RaceLine(pixels, width, height, right, 671, right - 10, 679, gold, 3);
            RaceLine(pixels, width, height, right - 10, 679, right, 687, gold, 3);
        }
        for (var column = 0; column < 10; column++)
        for (var row = 0; row < 2; row++)
        {
            var colour = (column + row) % 2 == 0 ? gold : 0x17171B;
            FillPictionaryPixels(pixels, width, height, 25 + column * 13, 665 + row * 16, 13, 16, colour);
            FillPictionaryPixels(pixels, width, height, 1125 + column * 13, 665 + row * 16, 13, 16, colour);
        }
        DrawChocoRaceBetStackTooltip(pixels);
        DrawChocoRaceWeatherPreferenceTooltip(pixels);
        DrawChocoRaceSlipTooltip(pixels);
        if (chocoRacePhase == ChocoRacePhase.Results)
            DrawChocoRaceResultsOverlayPixels(pixels);
        DrawChocoRaceSettingsOverlay(pixels);
        DrawChocoRaceHelpOverlay(pixels);
        return pixels;
    }

    private static int ChocoRaceHoverPulse(DateTime now) =>
        (int)Math.Round(2d + 2d * Math.Sin(now.TimeOfDay.TotalSeconds * 9d));

    private bool ChocoRaceClickFlash(int target, DateTime now) =>
        chocoRaceLastClickedTarget == target &&
        (now - chocoRaceLastClickUtc).TotalMilliseconds is >= 0d and < 450d;

    private static void DrawRaceDuck(byte[] pixels, int w, int h, int x, int y,
        uint colour, uint wing, uint eye, int frame = 0)
    {
        const uint orange = 0xE78342;
        RaceCircle(pixels, w, h, x - 4, y - 14, 16, colour);
        RaceCircle(pixels, w, h, x + 14, y - 28, 10, colour);
        RaceTriangle(pixels, w, h, x - 13, y - 22, x - 27, y - 19, x - 18, y - 8, colour);
        RaceTriangle(pixels, w, h, x + 22, y - 30, x + 35, y - 26, x + 22, y - 22, orange);
        RaceCircle(pixels, w, h, x - 5, y - 13 - frame * 2, 8, wing);
        RaceCircle(pixels, w, h, x + 17, y - 31, 2, eye);
        RaceLine(pixels, w, h, x - 10, y - 2, x - 12 + frame * 5, y + 1, orange, 2);
        RaceLine(pixels, w, h, x + 6, y - 2, x + 9 - frame * 5, y + 1, orange, 2);
    }

    private static void DrawRaceDuckLarge(byte[] pixels, int w, int h, int x, int y)
    {
        const uint yellow = 0xF5CB48, wing = 0xF9DE79, orange = 0xE78342;
        RaceCircle(pixels, w, h, x - 6, y - 23, 23, yellow);
        RaceCircle(pixels, w, h, x + 22, y - 44, 14, yellow);
        RaceTriangle(pixels, w, h, x - 19, y - 34, x - 48, y - 33, x - 27, y - 10, yellow);
        RaceTriangle(pixels, w, h, x + 34, y - 46, x + 53, y - 40, x + 34, y - 35, orange);
        RaceCircle(pixels, w, h, x - 7, y - 21, 12, wing);
        RaceCircle(pixels, w, h, x + 27, y - 48, 3, 0x232331);
        RaceLine(pixels, w, h, x - 15, y - 4, x - 20, y + 3, orange, 3);
        RaceLine(pixels, w, h, x + 7, y - 4, x + 12, y + 3, orange, 3);
    }

    private static void RaceCircle(byte[] pixels, int w, int h, int cx, int cy, int radius,
        uint gold, bool fill = true, int thickness = 1)
    {
        for (var py = Math.Max(0, cy - radius); py <= Math.Min(h - 1, cy + radius); py++)
        {
            var dy = py - cy;
            var outer = (int)Math.Sqrt(radius * radius - dy * dy);
            var inner = fill ? 0 : (int)Math.Sqrt(Math.Max(0, (radius - thickness) * (radius - thickness) - dy * dy));
            if (fill)
                FillPictionaryPixels(pixels, w, h, cx - outer, py, outer * 2 + 1, 1, gold);
            else if (Math.Abs(dy) > radius - thickness)
                FillPictionaryPixels(pixels, w, h, cx - outer, py, outer * 2 + 1, 1, gold);
            else
            {
                FillPictionaryPixels(pixels, w, h, cx - outer, py, outer - inner + 1, 1, gold);
                FillPictionaryPixels(pixels, w, h, cx + inner, py, outer - inner + 1, 1, gold);
            }
        }
    }

    private static void RaceTriangle(byte[] pixels, int w, int h,
        int ax, int ay, int bx, int by, int cx, int cy, uint colour)
    {
        var minX = Math.Max(0, Math.Min(ax, Math.Min(bx, cx)));
        var maxX = Math.Min(w - 1, Math.Max(ax, Math.Max(bx, cx)));
        var minY = Math.Max(0, Math.Min(ay, Math.Min(by, cy)));
        var maxY = Math.Min(h - 1, Math.Max(ay, Math.Max(by, cy)));
        var sign = (bx - ax) * (cy - ay) - (by - ay) * (cx - ax) >= 0 ? 1 : -1;
        for (var py = minY; py <= maxY; py++)
        for (var px = minX; px <= maxX; px++)
        {
            var e1 = sign * ((bx - ax) * (py - ay) - (by - ay) * (px - ax));
            var e2 = sign * ((cx - bx) * (py - by) - (cy - by) * (px - bx));
            var e3 = sign * ((ax - cx) * (py - cy) - (ay - cy) * (px - cx));
            if (e1 >= 0 && e2 >= 0 && e3 >= 0)
                FillPictionaryPixels(pixels, w, h, px, py, 1, 1, colour);
        }
    }

    private static void RaceLine(byte[] pixels, int w, int h, int x1, int y1, int x2, int y2,
        uint colour, int thickness)
    {
        var dx = x2 - x1;
        var dy = y2 - y1;
        var steps = Math.Max(Math.Abs(dx), Math.Abs(dy));
        for (var step = 0; step <= steps; step++)
        {
            var x = x1 + (steps == 0 ? 0 : dx * step / steps);
            var y = y1 + (steps == 0 ? 0 : dy * step / steps);
            FillPictionaryPixels(pixels, w, h, x - thickness / 2, y - thickness / 2,
                thickness, thickness, colour);
        }
    }
}

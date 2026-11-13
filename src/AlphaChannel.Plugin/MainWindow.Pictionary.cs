using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using AlphaChannel.Plugin.Video;

namespace AlphaChannel.Plugin;

// Watch Party Pictionary UI. Game state and transport live in the companion
// MainWindow.Pictionary.Game.cs partial; this file stays focused on presentation.
internal sealed partial class MainWindow
{
    private Guid? pictionaryChatFocusTargetId;
    private DateTime pictionaryChatFocusUntilUtc;

    private void DrawPictionaryPage()
    {
        lastActivitiesPage = HomePage.Pictionary;
        DrainPartyChat();
        if (PictionaryActive)
        {
            pictionaryShowSetup = false;
        }
        if (pictionaryShowSetup)
        {
            DrawPictionarySetupPage();
            return;
        }

        using var spacing = ImRaii.PushStyle(ImGuiStyleVar.ItemSpacing, UiVec(10f, 12f));

        DrawPictionaryRoundStatus();

        var available = ImGui.GetContentRegionAvail().X;
        var gap = Ui(12f);
        // The drawing toolbar needs enough horizontal room for colour, brush and canvas groups.
        // Stack the chat beneath it before either panel becomes cramped.
        var stack = available < Ui(900f);
        var sideWidth = stack ? available : Math.Clamp(available * 0.30f, Ui(250f), Ui(310f));
        var canvasWidth = stack ? available : available - sideWidth - gap;
        var mainHeight = Ui(550f);

        DrawPictionaryCanvasPanel(canvasWidth, mainHeight);

        if (!stack)
        {
            ImGui.SameLine(0f, gap);
        }

        DrawPictionarySidePanel(sideWidth, stack ? Ui(550f) : mainHeight);
        DrawPictionaryFooter();
        ImGui.Dummy(UiVec(0f, 12f));
    }

    private void DrawPictionaryRoundStatus()
    {
        var size = new Vector2(ImGui.GetContentRegionAvail().X, Ui(78f));
        using var colours = ImRaii.PushColor(ImGuiCol.ChildBg, CardBg).Push(ImGuiCol.Border, BorderSubtle);
        using var style = ImRaii.PushStyle(ImGuiStyleVar.ChildRounding, Ui(10f))
            .Push(ImGuiStyleVar.ChildBorderSize, Ui(1f))
            .Push(ImGuiStyleVar.WindowPadding, Vector2.Zero);

        if (UiFonts.Window(ImGui.GetFontSize(), ImGui.BeginChild("##pictionaryRoundStatus", size, true, ImGuiWindowFlags.NoScrollbar)))
        {
            var origin = ImGui.GetWindowPos();
            var drawList = ImGui.GetWindowDrawList();
            var centerY = origin.Y + size.Y * 0.5f;

            var roundLabel = PictionaryActive
                ? pictionaryPhase == PictionaryPhase.GameOver
                    ? "FINAL SCORES"
                    : $"ROUND {Math.Max(1, pictionaryRound)} OF {pictionaryRoundCount}"
                : "READY TO PLAY";
            DrawPictionaryPill(drawList, origin + UiVec(16f, 19f), roundLabel, Accent, Ui(142f));

            var firstDividerX = origin.X + Ui(174f);
            drawList.AddLine(new Vector2(firstDividerX, origin.Y + Ui(14f)), new Vector2(firstDividerX, origin.Y + size.Y - Ui(14f)), ImGui.GetColorU32(BorderSubtle), Ui(1f));

            var drawerName = string.IsNullOrWhiteSpace(pictionaryDrawerName) ? "Waiting for host" : pictionaryDrawerName;
            DrawPictionaryAvatar(drawList, new Vector2(firstDividerX + Ui(36f), centerY), Ui(20f), new Vector4(0.95f, 0.67f, 0.45f, 1f), drawerName[..1].ToUpperInvariant());
            DrawUsernameAt(drawList,new Vector2(firstDividerX + Ui(64f), centerY - Ui(11f)),drawerName,Vector4.One);
            var drawerSuffix = pictionaryPhase switch
            {
                PictionaryPhase.Countdown => "game starts soon",
                PictionaryPhase.ChoosingDrawer => "will draw next",
                PictionaryPhase.ChoosingWord => "is choosing",
                PictionaryPhase.Drawing => "is drawing",
                _ => string.Empty,
            };
            if (drawerSuffix.Length > 0)
            {
                drawList.AddText(new Vector2(firstDividerX + Ui(64f), centerY + Ui(8f)), ImGui.GetColorU32(MutedText), drawerSuffix);
            }

            var timerWidth = Ui(128f);
            var menuWidth = Ui(42f);
            var timerRight = origin.X + size.X - Ui(16f) - menuWidth - Ui(12f);
            var timerMin = new Vector2(timerRight - timerWidth, centerY - Ui(22f));
            drawList.AddRectFilled(timerMin, timerMin + new Vector2(timerWidth, Ui(44f)), ImGui.GetColorU32(new Vector4(0.11f, 0.10f, 0.23f, 1f)), Ui(22f));
            drawList.AddRect(timerMin, timerMin + new Vector2(timerWidth, Ui(44f)), ImGui.GetColorU32(AccentHover), Ui(22f), ImDrawFlags.None, Ui(1.5f));
            DrawPictionaryIcon(drawList, FontAwesomeIcon.Clock, timerMin + UiVec(13f, 12f), 1.05f, AccentHover);
            var timerText = pictionaryPhase is PictionaryPhase.Countdown or PictionaryPhase.ChoosingDrawer or PictionaryPhase.ChoosingWord or PictionaryPhase.Drawing or PictionaryPhase.RoundSummary
                ? FormatPictionaryTimer(PictionarySecondsRemaining)
                : "--:--";
            drawList.AddText(timerMin + UiVec(42f, 11f), ImGui.GetColorU32(Vector4.One), timerText);

            var menuMin = new Vector2(timerRight + Ui(12f), centerY - Ui(22f));
            drawList.AddRectFilled(menuMin, menuMin + new Vector2(menuWidth, Ui(44f)), ImGui.GetColorU32(new Vector4(0.08f, 0.10f, 0.17f, 1f)), Ui(8f));
            drawList.AddRect(menuMin, menuMin + new Vector2(menuWidth, Ui(44f)), ImGui.GetColorU32(BorderSubtle), Ui(8f));
            ImGui.SetCursorScreenPos(menuMin);
            if (ImGui.InvisibleButton("##pictionaryMenu", new Vector2(menuWidth, Ui(44f))))
            {
                ImGui.OpenPopup("##pictionaryMenuPopup");
            }
            DrawPictionaryIcon(drawList, FontAwesomeIcon.EllipsisH, menuMin + UiVec(12f, 13f), 1f, MutedText);
            if (UiFonts.Window(ImGui.GetFontSize(), ImGui.BeginPopup("##pictionaryMenuPopup")))
            {
                if (stream.Mode == StreamMode.Hosting && pictionaryPhase == PictionaryPhase.Drawing && ImGui.MenuItem("Skip round"))
                {
                    EndPictionaryRound();
                }
                if (stream.Mode == StreamMode.Hosting && PictionaryActive && ImGui.MenuItem("End game"))
                {
                    StopPictionaryGame();
                }
                if (stream.Mode != StreamMode.Hosting || !PictionaryActive)
                {
                    ImGui.TextDisabled("Host controls appear here");
                }
                ImGui.EndPopup();
            }

            var wordCenter = (firstDividerX + Ui(175f) + timerMin.X) * 0.5f;
            var word = string.IsNullOrWhiteSpace(PictionaryVisibleWord) ? "—" : PictionaryVisibleWord;
            var wordSize = ImGui.CalcTextSize(word);
            drawList.AddText(new Vector2(wordCenter - wordSize.X * 0.5f, centerY - Ui(15f)), ImGui.GetColorU32(Vector4.One), word);
            var letters = pictionaryWordLength > 0 ? $"{pictionaryWordLength} LETTERS" : "PICTIONARY";
            var lettersSize = ImGui.CalcTextSize(letters) * 0.88f;
            UiFonts.DrawText(drawList, ImGui.GetFontSize() * 0.88f, new Vector2(wordCenter - lettersSize.X * 0.5f, centerY + Ui(8f)), ImGui.GetColorU32(MutedText), letters);

            ImGui.Dummy(size);
        }
        ImGui.EndChild();
    }

    private void DrawPictionaryCanvasPanel(float width, float height)
    {
        using var colours = ImRaii.PushColor(ImGuiCol.ChildBg, CardBg).Push(ImGuiCol.Border, BorderSubtle);
        using var style = ImRaii.PushStyle(ImGuiStyleVar.ChildRounding, Ui(10f))
            .Push(ImGuiStyleVar.ChildBorderSize, Ui(1f))
            .Push(ImGuiStyleVar.WindowPadding, Vector2.Zero);

        if (UiFonts.Window(ImGui.GetFontSize(), ImGui.BeginChild("##pictionaryCanvasPanel", new Vector2(width, height), true, ImGuiWindowFlags.NoScrollbar)))
        {
            var origin = ImGui.GetWindowPos();
            var drawList = ImGui.GetWindowDrawList();

            var canDraw = pictionaryPhase == PictionaryPhase.Drawing && LocalIsPictionaryDrawer;
            DrawPictionaryPill(
                drawList,
                origin + UiVec(14f, 13f),
                canDraw ? "DRAW THE WORD" : PictionaryActive ? "GUESS THE DRAWING" : "PICTIONARY",
                Accent,
                Ui(172f));

            var instructionMin = origin + UiVec(198f, 13f);
            var instructionWidth = Math.Max(Ui(180f), width - Ui(212f));
            drawList.AddRectFilled(instructionMin, instructionMin + new Vector2(instructionWidth, Ui(36f)), ImGui.GetColorU32(new Vector4(0.10f, 0.13f, 0.21f, 1f)), Ui(8f));
            drawList.AddRect(instructionMin, instructionMin + new Vector2(instructionWidth, Ui(36f)), ImGui.GetColorU32(BorderSubtle), Ui(8f));
            GetPictionaryPageNotice(canDraw, out var instruction, out var instructionIcon, out var instructionColour);
            DrawPictionaryIcon(drawList, instructionIcon, instructionMin + UiVec(12f, 10f), 0.92f, instructionColour);
            drawList.PushClipRect(instructionMin + UiVec(36f, 2f), instructionMin + new Vector2(instructionWidth - Ui(8f), Ui(34f)), true);
            drawList.AddText(instructionMin + UiVec(36f, 9f), ImGui.GetColorU32(new Vector4(0.88f, 0.90f, 0.97f, 1f)), instruction);
            drawList.PopClipRect();

            DrawPictionaryDrawingControls(drawList, origin, width, canDraw);

            var canvasMin = origin + UiVec(14f, 169f);
            var canvasMax = origin + new Vector2(width - Ui(14f), height - Ui(14f));
            DrawPictionaryLiveCanvas(drawList, canvasMin, canvasMax, canDraw);
            ImGui.Dummy(new Vector2(width, height));
        }
        ImGui.EndChild();
    }

    private void DrawPictionaryDrawingControls(ImDrawListPtr drawList, Vector2 origin, float width, bool canDraw, bool teams = false)
    {
        var toolsMin = origin + UiVec(14f, 61f);
        var toolsMax = origin + new Vector2(width - Ui(14f), Ui(157f));
        drawList.AddRectFilled(toolsMin, toolsMax, ImGui.GetColorU32(new Vector4(0.055f, 0.07f, 0.12f, 1f)), Ui(8f));
        drawList.AddRect(toolsMin, toolsMax, ImGui.GetColorU32(BorderSubtle), Ui(8f));
        drawList.AddText(toolsMin + UiVec(16f, 13f), ImGui.GetColorU32(MutedText), "DRAWING CONTROLS");
        drawList.AddText(toolsMin + UiVec(16f, 44f), ImGui.GetColorU32(new Vector4(MutedText.X, MutedText.Y, MutedText.Z, canDraw ? 1f : 0.58f)), canDraw ? "Your drawing tools" : "Available on your turn");

        var controlsStartX = toolsMin.X + Math.Clamp(width * 0.25f, Ui(150f), Ui(180f));
        var swatchStart = new Vector2(controlsStartX, toolsMin.Y + Ui(44f));
        drawList.AddLine(
            new Vector2(controlsStartX - Ui(14f), toolsMin.Y + Ui(12f)),
            new Vector2(controlsStartX - Ui(14f), toolsMax.Y - Ui(12f)),
            ImGui.GetColorU32(BorderSubtle));
        drawList.AddText(new Vector2(controlsStartX, toolsMin.Y + Ui(12f)), ImGui.GetColorU32(MutedText), "COLOUR");
        var visibleColours = new[] { 1, 3, 5, 6, 8, 10, 11, 12, 14 };
        for (var visualIndex = 0; visualIndex < visibleColours.Length; visualIndex++)
        {
            var colourIndex = visibleColours[visualIndex];
            var x = swatchStart.X + Ui(25f) * visualIndex;
            var swatchMin = new Vector2(x, swatchStart.Y);
            ImGui.SetCursorScreenPos(swatchMin);
            ImGui.PushID(colourIndex);
            if (ImGui.InvisibleButton("##pictionaryColour", UiVec(20f, 20f)) && canDraw)
            {
                whiteboardColourIndex = colourIndex;
            }
            drawList.AddRectFilled(swatchMin, swatchMin + UiVec(20f, 20f), ImGui.GetColorU32(WhiteboardColours[colourIndex].Value), Ui(5f));
            if (whiteboardColourIndex == colourIndex)
            {
                drawList.AddRect(swatchMin - Vector2.One * Ui(2f), swatchMin + UiVec(22f, 22f), ImGui.GetColorU32(AccentHover), Ui(6f), ImDrawFlags.None, Ui(2f));
            }
            ImGui.PopID();
        }

        var brushGroupX = swatchStart.X + Ui(238f);
        drawList.AddLine(
            new Vector2(brushGroupX - Ui(12f), toolsMin.Y + Ui(12f)),
            new Vector2(brushGroupX - Ui(12f), toolsMax.Y - Ui(12f)),
            ImGui.GetColorU32(BorderSubtle));
        drawList.AddText(new Vector2(brushGroupX, toolsMin.Y + Ui(12f)), ImGui.GetColorU32(MutedText), "BRUSH SIZE");
        var brushStart = new Vector2(brushGroupX, toolsMin.Y + Ui(43f));
        for (var index = 0; index < WhiteboardBrushSizes.Length; index++)
        {
            var brushMin = brushStart + new Vector2(Ui(27f) * index, 0f);
            ImGui.SetCursorScreenPos(brushMin);
            ImGui.PushID(1000 + index);
            if (ImGui.InvisibleButton("##pictionaryBrush", UiVec(22f, 22f)) && canDraw)
            {
                whiteboardBrushWidth = WhiteboardBrushSizes[index];
            }
            var selected = MathF.Abs(whiteboardBrushWidth - WhiteboardBrushSizes[index]) < 0.1f;
            drawList.AddCircleFilled(
                brushMin + UiVec(11f, 11f),
                Ui(2f + index * 1.5f),
                ImGui.GetColorU32(selected ? AccentHover : MutedText));
            ImGui.PopID();
        }
        if (teams) return;

        var actionsX = brushGroupX + Ui(110f);
        drawList.AddLine(
            new Vector2(actionsX - Ui(12f), toolsMin.Y + Ui(12f)),
            new Vector2(actionsX - Ui(12f), toolsMax.Y - Ui(12f)),
            ImGui.GetColorU32(BorderSubtle));
        drawList.AddText(new Vector2(actionsX, toolsMin.Y + Ui(12f)), ImGui.GetColorU32(MutedText), "CANVAS");
        var undoMin = new Vector2(actionsX, toolsMin.Y + Ui(39f));
        ImGui.SetCursorScreenPos(undoMin);
        if (!canDraw)
        {
            UiDisabled.Begin();
        }
        if (ImGui.SmallButton("Undo##pictionaryUndo") && canDraw)
        {
            UndoWhiteboardStroke();
        }
        ImGui.SameLine(0f, Ui(5f));
        if (ImGui.SmallButton("Clear##pictionaryClear") && canDraw && whiteboardSessionId is not null)
        {
            SendAndApplyWhiteboardCommand($"C|{whiteboardSessionId}");
        }
        if (!canDraw)
        {
            UiDisabled.End();
        }

    }

    private void DrawPictionaryLiveCanvas(ImDrawListPtr drawList, Vector2 min, Vector2 max, bool canDraw)
    {
        var size = max - min;
        var hovered = false;
        if (canDraw)
        {
            // Only register the full-canvas hit target while drawing. Registering it during
            // inactive/selection states places an invisible item beneath the centered action
            // and word-choice buttons, which can claim their mouse clicks first.
            ImGui.SetCursorScreenPos(min);
            ImGui.InvisibleButton("##pictionaryLiveCanvas", size, ImGuiButtonFlags.MouseButtonLeft);
            hovered = ImGui.IsItemHovered();
        }
        drawList.AddRectFilled(min, max, ImGui.GetColorU32(new Vector4(0.015f, 0.018f, 0.028f, 1f)), Ui(8f));
        drawList.PushClipRect(min, max, true);

        foreach (var stroke in VisibleWhiteboardStrokes)
        {
            var colour = ImGui.GetColorU32(WhiteboardColours[stroke.ColourIndex].Value);
            for (var index = 1; index < stroke.Points.Count; index++)
            {
                drawList.AddLine(
                    min + stroke.Points[index - 1] * size,
                    min + stroke.Points[index] * size,
                    colour,
                    Math.Max(1f, Ui(stroke.Width) * size.Y / WhiteboardRenderer.Height));
            }
        }

        if (!PictionaryActive)
        {
            var isHost = stream.Mode == StreamMode.Hosting;
            var hasEnoughPlayers = isHost && stream.Roster.Length > 0;
            var heading = stream.Mode == StreamMode.None
                ? "Join or host a Watch Party"
                : isHost
                    ? hasEnoughPlayers ? $"{stream.Roster.Length + 1} players ready" : "Waiting for another player"
                    : "Waiting for the host to start";
            var detail = stream.Mode == StreamMode.None
                ? "Pictionary is played inside a Watch Party."
                : isHost
                    ? hasEnoughPlayers
                        ? "Everyone is ready for a 10-round game."
                        : "Pictionary needs at least 2 players. Invite someone to your Watch Party."
                    : "Only the Watch Party host can start Pictionary.";
            var buttonLabel = isHost
                ? hasEnoughPlayers ? "Start Pictionary" : "Needs at least 1 viewer"
                : stream.Mode == StreamMode.None ? "Watch Party required" : "Host starts the game";
            DrawPictionaryCenteredAction(drawList, min, max, heading, detail, buttonLabel, hasEnoughPlayers, StartPictionaryGame);
        }
        else if (pictionaryPhase == PictionaryPhase.Countdown)
        {
            DrawPictionaryCenteredMessage(drawList, min, max, "PICTIONARY STARTS IN",
                $"{PictionarySecondsRemaining} SECONDS");
        }
        else if (pictionaryPhase == PictionaryPhase.ChoosingDrawer)
        {
            var shownName = GetPictionarySelectionDisplayName();
            DrawPictionaryCenteredMessage(drawList, min, max, pictionaryRandomDrawerSelection ? "CHOOSING THE NEXT DRAWER..." : "FIRST GUESSER DRAWS NEXT", shownName);
        }
        else if (pictionaryPhase == PictionaryPhase.ChoosingWord)
        {
            if (LocalIsPictionaryDrawer && pictionaryWordChoices.Length == 3)
            {
                DrawPictionaryWordChoices(drawList, min, max);
            }
            else
            {
                DrawPictionaryCenteredMessage(drawList, min, max, $"{pictionaryDrawerName.ToUpperInvariant()} IS CHOOSING", "Three words to choose from");
            }
        }
        else if (pictionaryPhase == PictionaryPhase.RoundSummary)
        {
            DrawPictionaryCenteredMessage(drawList, min, max, "THE ANSWER WAS", pictionaryRevealedWord.ToUpperInvariant());
        }
        else if (pictionaryPhase == PictionaryPhase.GameOver)
        {
            var leader = pictionaryScores.OrderByDescending(pair => pair.Value).FirstOrDefault();
            var winner = string.IsNullOrEmpty(leader.Key) ? "Game complete" : $"{pictionaryPlayerNames.GetValueOrDefault(leader.Key, "Player")} wins with {leader.Value} points";
            DrawPictionaryCenteredAction(
                drawList,
                min,
                max,
                winner,
                stream.Mode == StreamMode.Hosting ? "Start another 10-round match." : "The host can start another match.",
                stream.Mode == StreamMode.Hosting ? "Play again" : "Final scores",
                stream.Mode == StreamMode.Hosting,
                StartPictionaryGame);
        }

        if (canDraw)
        {
            var chipMin = min + UiVec(12f, 12f);
            drawList.AddRectFilled(chipMin, chipMin + UiVec(172f, 30f), ImGui.GetColorU32(new Vector4(0.035f, 0.045f, 0.075f, 0.92f)), Ui(15f));
            drawList.AddCircleFilled(chipMin + UiVec(15f, 15f), Ui(5f), ImGui.GetColorU32(WhiteboardColours[whiteboardColourIndex].Value));
            DrawUsernameAt(drawList,chipMin + UiVec(28f, 7f),$"{pictionaryDrawerName} • {GetWhiteboardBrushName()} brush",Vector4.One);
        }
        drawList.PopClipRect();
        drawList.AddRect(min, max, ImGui.GetColorU32(BorderSubtle), Ui(8f));

        if (!canDraw || !hovered)
        {
            if (!ImGui.IsMouseDown(ImGuiMouseButton.Left))
            {
                FinishWhiteboardStroke();
            }
            return;
        }

        var mouse = ImGui.GetMousePos();
        var point = new Vector2(Math.Clamp((mouse.X - min.X) / size.X, 0f, 1f), Math.Clamp((mouse.Y - min.Y) / size.Y, 0f, 1f));
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

    private string GetPictionarySelectionDisplayName()
    {
        var remaining = Math.Max(0, (pictionaryPhaseEndsUtc - DateTime.UtcNow).TotalSeconds);
        if (!pictionaryRandomDrawerSelection || remaining < 0.45)
        {
            return pictionaryDrawerName;
        }
        var names = pictionaryActivePlayers
            .Select(id => pictionaryPlayerNames.GetValueOrDefault(id, "Player"))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (names.Length == 0)
        {
            return pictionaryDrawerName;
        }
        var interval = remaining > 1.5 ? 110L : remaining > 0.8 ? 180L : 280L;
        var index = (int)((DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / interval) % names.Length);
        return names[index];
    }

    private static void DrawPictionaryCenteredMessage(ImDrawListPtr drawList, Vector2 min, Vector2 max, string heading, string value)
    {
        var center = (min + max) * 0.5f;
        var headingSize = ImGui.CalcTextSize(heading);
        var valueSize = ImGui.CalcTextSize(value);
        drawList.AddText(center - new Vector2(headingSize.X * 0.5f, Ui(30f)), ImGui.GetColorU32(MutedText), heading);
        drawList.AddText(center - new Vector2(valueSize.X * 0.5f, -Ui(4f)), ImGui.GetColorU32(Vector4.One), value);
    }

    private void DrawPictionaryCenteredAction(
        ImDrawListPtr drawList,
        Vector2 min,
        Vector2 max,
        string heading,
        string detail,
        string buttonLabel,
        bool enabled,
        Action action)
    {
        var center = (min + max) * 0.5f;
        var headingSize = ImGui.CalcTextSize(heading);
        var detailSize = ImGui.CalcTextSize(detail);
        drawList.AddText(center - new Vector2(headingSize.X * 0.5f, Ui(60f)), ImGui.GetColorU32(Vector4.One), heading);
        drawList.AddText(center - new Vector2(detailSize.X * 0.5f, Ui(31f)), ImGui.GetColorU32(MutedText), detail);
        var buttonSize = UiVec(210f, 44f);
        var buttonMin = center - new Vector2(buttonSize.X * 0.5f, -Ui(15f));
        ImGui.SetCursorScreenPos(buttonMin);
        if (!enabled)
        {
            UiDisabled.Begin();
        }
        var clicked = ImGui.Button(buttonLabel + "##pictionaryCenteredAction", buttonSize);
        if (!enabled)
        {
            UiDisabled.End();
        }
        if (clicked)
        {
            action();
        }
    }

    private void DrawPictionaryWordChoices(ImDrawListPtr drawList, Vector2 min, Vector2 max)
    {
        var center = (min + max) * 0.5f;
        var heading = "CHOOSE A WORD";
        var headingSize = ImGui.CalcTextSize(heading);
        drawList.AddText(new Vector2(center.X - headingSize.X * 0.5f, center.Y - Ui(62f)), ImGui.GetColorU32(MutedText), heading);
        var totalWidth = Math.Min(max.X - min.X - Ui(40f), Ui(450f));
        var gap = Ui(10f);
        var buttonWidth = (totalWidth - gap * 2f) / 3f;
        var startX = center.X - totalWidth * 0.5f;
        for (var index = 0; index < pictionaryWordChoices.Length; index++)
        {
            ImGui.SetCursorScreenPos(new Vector2(startX + index * (buttonWidth + gap), center.Y - Ui(16f)));
            if (ImGui.Button($"{pictionaryWordChoices[index]}##pictionaryWord{index}", new Vector2(buttonWidth, Ui(48f))))
            {
                SubmitPictionaryWordChoice(index);
            }
        }
    }

    private void SubmitPictionaryChatEntry()
    {
        if (LocalCanSubmitPictionaryGuess)
        {
            SubmitPictionaryGuess();
            return;
        }

        var message = pictionaryGuessInput.Trim();
        pictionaryGuessInput = string.Empty;
        if (message.Length > 0)
        {
            _ = stream.SendChatAsync(message);
        }
    }

    private static Vector4 PictionaryAvatarColour(string userId)
    {
        var hash = StringComparer.Ordinal.GetHashCode(userId ?? string.Empty);
        var colours = new[]
        {
            new Vector4(0.67f, 0.45f, 1f, 1f),
            new Vector4(0.42f, 0.65f, 1f, 1f),
            new Vector4(0.98f, 0.42f, 0.66f, 1f),
            new Vector4(0.20f, 0.82f, 0.74f, 1f),
            new Vector4(1f, 0.68f, 0.28f, 1f),
        };
        return colours[(hash & int.MaxValue) % colours.Length];
    }

    private void DrawPictionarySampleCanvas(ImDrawListPtr drawList, Vector2 min, Vector2 max)
    {
        drawList.AddRectFilled(min, max, ImGui.GetColorU32(new Vector4(0.015f, 0.018f, 0.028f, 1f)), Ui(8f));
        drawList.AddRect(min, max, ImGui.GetColorU32(BorderSubtle), Ui(8f));

        var size = max - min;
        Vector2 P(float x, float y) => min + new Vector2(size.X * x, size.Y * y);
        var yellow = ImGui.GetColorU32(new Vector4(1f, 0.80f, 0.10f, 1f));
        var cyan = ImGui.GetColorU32(new Vector4(0.10f, 0.84f, 0.94f, 1f));
        var pink = ImGui.GetColorU32(new Vector4(0.98f, 0.35f, 0.67f, 1f));
        var stroke = Ui(4f);

        var head = P(0.50f, 0.56f);
        var radius = Math.Min(size.X * 0.18f, size.Y * 0.27f);
        drawList.AddCircle(head, radius, yellow, 48, stroke);
        drawList.AddTriangle(head + new Vector2(-radius * 0.82f, -radius * 0.54f), head + new Vector2(-radius * 0.58f, -radius * 1.30f), head + new Vector2(-radius * 0.14f, -radius * 0.82f), yellow, stroke);
        drawList.AddTriangle(head + new Vector2(radius * 0.14f, -radius * 0.82f), head + new Vector2(radius * 0.58f, -radius * 1.30f), head + new Vector2(radius * 0.82f, -radius * 0.54f), yellow, stroke);
        drawList.AddCircleFilled(head + new Vector2(-radius * 0.33f, -radius * 0.10f), Ui(3f), yellow);
        drawList.AddCircleFilled(head + new Vector2(radius * 0.33f, -radius * 0.10f), Ui(3f), yellow);
        drawList.AddTriangleFilled(head + new Vector2(0f, radius * 0.10f), head + new Vector2(-Ui(5f), radius * 0.02f), head + new Vector2(Ui(5f), radius * 0.02f), yellow);
        drawList.AddBezierCubic(head + new Vector2(0f, radius * 0.13f), head + new Vector2(-radius * 0.08f, radius * 0.32f), head + new Vector2(-radius * 0.24f, radius * 0.28f), head + new Vector2(-radius * 0.28f, radius * 0.18f), yellow, stroke * 0.75f);
        drawList.AddBezierCubic(head + new Vector2(0f, radius * 0.13f), head + new Vector2(radius * 0.08f, radius * 0.32f), head + new Vector2(radius * 0.24f, radius * 0.28f), head + new Vector2(radius * 0.28f, radius * 0.18f), yellow, stroke * 0.75f);
        for (var offset = -1; offset <= 1; offset += 2)
        {
            var side = offset;
            drawList.AddLine(head + new Vector2(radius * 0.30f * side, radius * 0.16f), head + new Vector2(radius * 0.92f * side, radius * 0.04f), yellow, stroke * 0.65f);
            drawList.AddLine(head + new Vector2(radius * 0.32f * side, radius * 0.28f), head + new Vector2(radius * 0.95f * side, radius * 0.35f), yellow, stroke * 0.65f);
        }
        drawList.AddBezierCubic(P(0.28f, 0.86f), P(0.31f, 0.66f), P(0.38f, 0.73f), head + new Vector2(-radius * 0.45f, radius * 0.84f), yellow, stroke);
        drawList.AddBezierCubic(head + new Vector2(radius * 0.45f, radius * 0.84f), P(0.69f, 0.72f), P(0.68f, 0.90f), P(0.72f, 0.86f), yellow, stroke);
        drawList.AddBezierCubic(P(0.72f, 0.86f), P(0.86f, 0.78f), P(0.84f, 0.49f), P(0.76f, 0.57f), yellow, stroke);
        drawList.AddLine(P(0.13f, 0.34f), P(0.20f, 0.42f), cyan, stroke);
        drawList.AddLine(P(0.12f, 0.47f), P(0.20f, 0.51f), cyan, stroke);
        drawList.AddLine(P(0.13f, 0.60f), P(0.20f, 0.56f), cyan, stroke);
        DrawActivitiesHeart(drawList, P(0.78f, 0.25f), size * new Vector2(0.055f, 0.17f), pink, stroke);
    }

    private void DrawPictionarySidePanel(float width, float height)
    {
        using var colours = ImRaii.PushColor(ImGuiCol.ChildBg, CardBg).Push(ImGuiCol.Border, BorderSubtle);
        using var style = ImRaii.PushStyle(ImGuiStyleVar.ChildRounding, Ui(10f))
            .Push(ImGuiStyleVar.ChildBorderSize, Ui(1f))
            .Push(ImGuiStyleVar.WindowPadding, Vector2.Zero);

        if (UiFonts.Window(ImGui.GetFontSize(), ImGui.BeginChild("##pictionarySidePanel", new Vector2(width, height), true, ImGuiWindowFlags.NoScrollbar)))
        {
            var origin = ImGui.GetWindowPos();
            var drawList = ImGui.GetWindowDrawList();
            var padding = Ui(12f);
            var answerHeight = Ui(128f);
            var answerMin = origin + new Vector2(padding, height - answerHeight - padding);
            var answerMax = origin + new Vector2(width - padding, height - padding);

            DrawPictionaryPermanentScores(drawList, origin, width, answerMin.Y - origin.Y - Ui(10f));
            DrawPictionaryAnswerComposer(drawList, answerMin, answerMax);

            ImGui.SetCursorScreenPos(origin);
            ImGui.Dummy(new Vector2(width, height));
        }
        ImGui.EndChild();
    }

    private void DrawPictionaryPermanentScores(ImDrawListPtr drawList, Vector2 origin, float width, float height)
    {
        DrawPictionaryIcon(
            drawList,
            FontAwesomeIcon.Trophy,
            origin + UiVec(14f, 17f),
            0.92f,
            AccentHover);
        drawList.AddText(
            origin + UiVec(38f, 15f),
            ImGui.GetColorU32(new Vector4(0.90f, 0.92f, 0.98f, 1f)),
            "SCOREBOARD");
        drawList.AddLine(
            origin + UiVec(12f, 43f),
            origin + new Vector2(width - Ui(12f), Ui(43f)),
            ImGui.GetColorU32(new Vector4(BorderSubtle.X, BorderSubtle.Y, BorderSubtle.Z, 0.70f)),
            Ui(1f));

        var ordered = pictionaryScores
            .OrderByDescending(pair => pair.Value)
            .ThenBy(pair => pictionaryPlayerNames.GetValueOrDefault(pair.Key, "Player"), StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var leader = ordered.FirstOrDefault();
        var leaderName = string.IsNullOrWhiteSpace(leader.Key)
            ? "No scores yet"
            : pictionaryPlayerNames.GetValueOrDefault(leader.Key, "Player");
        var leaderScore = string.IsNullOrWhiteSpace(leader.Key)
            ? "The leaderboard will appear here"
            : leader.Value == 1 ? "1 point" : $"{leader.Value} points";

        var leaderMin = origin + UiVec(12f, 54f);
        var leaderMax = origin + new Vector2(width - Ui(12f), Ui(126f));
        drawList.AddRectFilled(leaderMin, leaderMax, ImGui.GetColorU32(new Vector4(0.08f, 0.11f, 0.19f, 1f)), Ui(8f));
        drawList.AddRect(leaderMin, leaderMax, ImGui.GetColorU32(BorderSubtle), Ui(8f));
        DrawPictionaryIcon(drawList, FontAwesomeIcon.Trophy, leaderMin + UiVec(14f, 20f), 1.25f, new Vector4(1f, 0.78f, 0.12f, 1f));
        drawList.AddText(leaderMin + UiVec(43f, 11f), ImGui.GetColorU32(MutedText), "ROUND LEADER");
        DrawUsernameAt(drawList,leaderMin + UiVec(43f, 34f),leaderName,pictionaryGuessedPlayers.Contains(leader.Key) ? Good : Vector4.One,leader.Key);
        var leaderScoreSize = ImGui.CalcTextSize(leaderScore);
        drawList.AddText(new Vector2(leaderMax.X - leaderScoreSize.X - Ui(12f), leaderMin.Y + Ui(34f)), ImGui.GetColorU32(MutedText), leaderScore);

        var listMin = origin + UiVec(12f, 138f);
        var listHeight = Math.Max(Ui(44f), height - Ui(150f));
        ImGui.SetCursorScreenPos(listMin);
        using var childColours = ImRaii.PushColor(ImGuiCol.ChildBg, new Vector4(0f, 0f, 0f, 0f));
        using var childStyle = ImRaii.PushStyle(ImGuiStyleVar.WindowPadding, Vector2.Zero)
            .Push(ImGuiStyleVar.ItemSpacing, UiVec(0f, 5f));
        if (UiFonts.Window(ImGui.GetFontSize(), ImGui.BeginChild("##pictionaryPermanentScoreList", new Vector2(width - Ui(24f), listHeight), false)))
        {
            var listOrigin = ImGui.GetWindowPos();
            var listDraw = ImGui.GetWindowDrawList();
            if (ordered.Length == 0)
            {
                listDraw.AddText(listOrigin + UiVec(8f, 10f), ImGui.GetColorU32(MutedText), "No players have scored yet.");
                ImGui.Dummy(UiVec(0f, 38f));
            }
            else
            {
                for (var index = 0; index < ordered.Length; index++)
                {
                    var player = ordered[index];
                    var name = pictionaryPlayerNames.GetValueOrDefault(player.Key, "Player");
                    DrawPictionaryScoreRow(
                        listDraw,
                        listOrigin + new Vector2(0f, Ui(42f) * index),
                        width - Ui(36f),
                        index + 1,
                        name,
                        player.Value == 1 ? "1 point" : $"{player.Value} points",
                        PictionaryAvatarColour(player.Key),
                        string.Equals(player.Key, pictionaryDrawerId, StringComparison.Ordinal),
                        pictionaryGuessedPlayers.Contains(player.Key));
                }
                ImGui.Dummy(new Vector2(0f, Ui(42f) * ordered.Length));
            }
        }
        ImGui.EndChild();
    }

    private void DrawPictionaryAnswerComposer(ImDrawListPtr drawList, Vector2 min, Vector2 max, bool teams = false)
    {
        drawList.AddRectFilled(min, max, ImGui.GetColorU32(new Vector4(0.055f, 0.07f, 0.12f, 1f)), Ui(9f));
        drawList.AddRect(min, max, ImGui.GetColorU32(BorderSubtle), Ui(9f));
        drawList.AddText(min + UiVec(12f, 10f), ImGui.GetColorU32(MutedText), "SUBMIT YOUR ANSWER");

        var canSubmit = teams ? TeamsCanGuess : LocalCanSubmitPictionaryGuess;
        var hint = teams ? TeamsCanGuess ? "Type your answer..." : TeamsLocalDrawerColour >= 0 ? "You are drawing this round" : "Waiting for the next round" : canSubmit
            ? "Type your answer..."
            : pictionaryPhase == PictionaryPhase.Drawing && LocalIsPictionaryDrawer
                ? "You are drawing this round"
                : pictionaryPhase == PictionaryPhase.Drawing &&
                  (pictionaryGuessedPlayers.Contains(LocalPictionaryUserId) || LocalPictionaryAnswerResult is true)
                    ? "Answer locked in"
                    : pictionaryPhase == PictionaryPhase.Drawing && !pictionaryActivePlayers.Contains(LocalPictionaryUserId)
                        ? "Spectating until next round"
                        : "Waiting for the next round";

        var inputMin = min + UiVec(12f, 35f);
        var sendSize = Ui(42f);
        var inputWidth = max.X - min.X - Ui(32f) - sendSize;
        ImGui.SetCursorScreenPos(inputMin);
        ImGui.SetNextItemWidth(inputWidth);
        bool enter;
        if (!canSubmit)
            UiDisabled.Begin();
        using (ImRaii.PushColor(ImGuiCol.FrameBg, new Vector4(0.035f, 0.045f, 0.08f, 1f))
                   .Push(ImGuiCol.FrameBgHovered, new Vector4(0.065f, 0.075f, 0.13f, 1f))
                   .Push(ImGuiCol.FrameBgActive, new Vector4(0.075f, 0.085f, 0.15f, 1f))
                   .Push(ImGuiCol.Border, new Vector4(AccentHover.X, AccentHover.Y, AccentHover.Z, canSubmit ? 0.90f : 0.35f)))
        using (ImRaii.PushStyle(ImGuiStyleVar.FrameRounding, Ui(8f))
                   .Push(ImGuiStyleVar.FrameBorderSize, Ui(1.5f))
                   .Push(ImGuiStyleVar.FramePadding, UiVec(10f, 11f)))
        {
            enter = ImGui.InputTextWithHint("##pictionaryAnswerOnlyInput", hint, ref (teams ? ref teamsGuessInput : ref pictionaryGuessInput), teams ? 80 : 280, ImGuiInputTextFlags.EnterReturnsTrue);
        }

        var sendMin = new Vector2(inputMin.X + inputWidth + Ui(8f), inputMin.Y);
        ImGui.SetCursorScreenPos(sendMin);
        var send = ImGui.InvisibleButton("##submitPictionaryAnswer", new Vector2(sendSize, Ui(42f)));
        if (!canSubmit)
            UiDisabled.End();
        var sendHovered = canSubmit && ImGui.IsItemHovered();
        var sendColour = canSubmit
            ? sendHovered ? AccentHover : Accent
            : new Vector4(Accent.X, Accent.Y, Accent.Z, 0.35f);
        drawList.AddRectFilled(sendMin, sendMin + new Vector2(sendSize, Ui(42f)), ImGui.GetColorU32(sendColour), Ui(8f));
        DrawPictionaryIcon(drawList, FontAwesomeIcon.PaperPlane, sendMin + UiVec(11f, 11f), 1.02f, new Vector4(1f, 1f, 1f, canSubmit ? 1f : 0.45f));

        if (canSubmit && (enter || send))
        {
            if (teams && !string.IsNullOrWhiteSpace(teamsGuessInput)) SubmitTeamsGuess();
            else if (!teams && !string.IsNullOrWhiteSpace(pictionaryGuessInput)) SubmitPictionaryGuess();
        }

        var answerResult = teams ? teamsFeedback.Length > 0 ? (bool?)false : null : LocalPictionaryAnswerResult;
        if (answerResult is true)
        {
            DrawPictionaryIcon(drawList, FontAwesomeIcon.CheckCircle, min + UiVec(12f, 91f), 0.86f, Good);
            drawList.AddText(min + UiVec(34f, 90f), ImGui.GetColorU32(Good), "Correct — answer locked in");
        }
        else if (answerResult is false)
        {
            var incorrect = new Vector4(1f, 0.34f, 0.39f, 1f);
            DrawPictionaryIcon(drawList, FontAwesomeIcon.TimesCircle, min + UiVec(12f, 91f), 0.86f, incorrect);
            drawList.AddText(min + UiVec(34f, 90f), ImGui.GetColorU32(incorrect), "Incorrect — try again");
        }
        else
        {
            drawList.AddText(min + UiVec(12f, 91f), ImGui.GetColorU32(MutedText), "Answers are private until guessed correctly.");
        }
    }

    private void GetPictionaryPageNotice(bool canDraw, out string text, out FontAwesomeIcon icon, out Vector4 colour)
    {
        var notice = pictionaryFeed.LastOrDefault(item =>
            item.System || item.Success || item.Text.EndsWith("is drawing!", StringComparison.OrdinalIgnoreCase));
        if (notice is not null && DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - notice.CreatedUnixMs <= 6500)
        {
            text = notice.Text;
            icon = notice.Success ? FontAwesomeIcon.CheckCircle : notice.System ? FontAwesomeIcon.Clock : FontAwesomeIcon.InfoCircle;
            colour = notice.Success ? Good : notice.System ? new Vector4(1f, 0.72f, 0.22f, 1f) : AccentHover;
            return;
        }

        icon = FontAwesomeIcon.InfoCircle;
        colour = new Vector4(0.43f, 0.68f, 1f, 1f);
        text = pictionaryPhase switch
        {
            PictionaryPhase.Countdown => $"Pictionary starts in {PictionarySecondsRemaining} seconds",
            PictionaryPhase.ChoosingDrawer => "Choosing the next drawer",
            PictionaryPhase.ChoosingWord when LocalIsPictionaryDrawer => "Choose one of three words",
            PictionaryPhase.ChoosingWord => $"{pictionaryDrawerName} is choosing a word",
            PictionaryPhase.Drawing when canDraw => "Draw the word without writing it",
            PictionaryPhase.Drawing when pictionaryGuessedPlayers.Contains(LocalPictionaryUserId) => "You guessed correctly",
            PictionaryPhase.Drawing => "Submit your answer on the right or directly on the TV",
            PictionaryPhase.RoundSummary when !string.IsNullOrWhiteSpace(pictionaryRevealedWord) => $"The answer was {pictionaryRevealedWord}",
            PictionaryPhase.GameOver => "Game over — final scores",
            _ when stream.Mode == StreamMode.Hosting && stream.Roster.Length > 0 => "Start a game for your Watch Party",
            _ when stream.Mode == StreamMode.Hosting => "At least 2 players are required to start",
            _ => "Waiting for the Watch Party host to start",
        };
    }

    private void DrawPictionaryScoreboard(float width, float height)
    {
        using var colours = ImRaii.PushColor(ImGuiCol.ChildBg, CardBg).Push(ImGuiCol.Border, BorderSubtle);
        using var style = ImRaii.PushStyle(ImGuiStyleVar.ChildRounding, Ui(10f))
            .Push(ImGuiStyleVar.ChildBorderSize, Ui(1f))
            .Push(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        if (UiFonts.Window(ImGui.GetFontSize(), ImGui.BeginChild("##pictionaryScoreboard", new Vector2(width, height), true, ImGuiWindowFlags.NoScrollbar)))
        {
            var origin = ImGui.GetWindowPos();
            var drawList = ImGui.GetWindowDrawList();
            DrawPictionaryPill(drawList, origin + UiVec(14f, 12f), "SCOREBOARD", new Vector4(0.30f, 0.22f, 0.62f, 1f), Ui(126f));

            DrawPictionaryScoreRow(drawList, origin + UiVec(12f, 58f), width - Ui(24f), 1, "Kodie", "3 wins", new Vector4(0.95f, 0.67f, 0.45f, 1f), true, false);
            DrawPictionaryScoreRow(drawList, origin + UiVec(12f, 96f), width - Ui(24f), 2, "Mattie", "2 wins", new Vector4(0.42f, 0.65f, 1f, 1f), false, true);
            DrawPictionaryScoreRow(drawList, origin + UiVec(12f, 134f), width - Ui(24f), 3, "Luna", "1 win", new Vector4(0.98f, 0.42f, 0.66f, 1f), false, false);
            DrawPictionaryScoreRow(drawList, origin + UiVec(12f, 172f), width - Ui(24f), 4, "Taro", "0 wins", new Vector4(0.55f, 0.64f, 0.80f, 1f), false, false);
            ImGui.Dummy(new Vector2(width, height));
        }
        ImGui.EndChild();
    }

    private void DrawPictionaryScoreRow(ImDrawListPtr drawList, Vector2 min, float width, int rank, string name, string score, Vector4 avatarColour, bool drawing, bool guessed)
    {
        var max = min + new Vector2(width, Ui(34f));
        if (rank == 1)
        {
            drawList.AddRectFilled(min, max, ImGui.GetColorU32(new Vector4(0.09f, 0.12f, 0.20f, 1f)), Ui(7f));
        }
        else
        {
            drawList.AddLine(new Vector2(min.X, max.Y), new Vector2(max.X, max.Y), ImGui.GetColorU32(new Vector4(BorderSubtle.X, BorderSubtle.Y, BorderSubtle.Z, 0.55f)), Ui(1f));
        }

        if (rank <= 3)
        {
            DrawPictionaryIcon(drawList, FontAwesomeIcon.Trophy, min + UiVec(8f, 9f), 0.88f, rank == 1 ? new Vector4(1f, 0.78f, 0.12f, 1f) : MutedText);
        }
        drawList.AddText(min + UiVec(34f, 9f), ImGui.GetColorU32(MutedText), rank.ToString());
        DrawPictionaryAvatar(drawList, min + UiVec(67f, 17f), Ui(13f), avatarColour, name[..1]);
        DrawUsernameAt(drawList,min + UiVec(88f, 9f),name,guessed ? Good : Vector4.One);
        var scoreSize = ImGui.CalcTextSize(score);
        drawList.AddText(new Vector2(max.X - scoreSize.X - Ui(28f), min.Y + Ui(9f)), ImGui.GetColorU32(MutedText), score);
        if (drawing)
        {
            DrawPictionaryIcon(drawList, FontAwesomeIcon.PaintBrush, max - UiVec(18f, 25f), 0.9f, AccentHover);
        }
        else if (guessed)
        {
            drawList.AddCircleFilled(max - UiVec(11f, 17f), Ui(8f), ImGui.GetColorU32(Good));
            var check = FontAwesomeIcon.Check.ToIconString();
            UiFonts.DrawIcon(drawList, ImGui.GetFontSize() * 0.72f, max - UiVec(15f, 23f), ImGui.GetColorU32(new Vector4(0.02f, 0.12f, 0.08f, 1f)), check);
        }
    }

    private void DrawPictionaryChat(float width, float height)
    {
        using var colours = ImRaii.PushColor(ImGuiCol.ChildBg, CardBg).Push(ImGuiCol.Border, BorderSubtle);
        using var style = ImRaii.PushStyle(ImGuiStyleVar.ChildRounding, Ui(10f))
            .Push(ImGuiStyleVar.ChildBorderSize, Ui(1f))
            .Push(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        if (UiFonts.Window(ImGui.GetFontSize(), ImGui.BeginChild("##pictionaryChat", new Vector2(width, height), true, ImGuiWindowFlags.NoScrollbar)))
        {
            var origin = ImGui.GetWindowPos();
            var drawList = ImGui.GetWindowDrawList();
            DrawPictionaryPill(drawList, origin + UiVec(14f, 12f), "PARTY CHAT", new Vector4(0.30f, 0.22f, 0.62f, 1f), Ui(116f));
            DrawPictionaryIcon(drawList, FontAwesomeIcon.ExternalLinkAlt, origin + new Vector2(width - Ui(33f), Ui(21f)), 0.92f, new Vector4(0.65f, 0.72f, 0.90f, 1f));

            var rowWidth = width - Ui(24f);
            DrawPictionaryChatRow(drawList, origin + UiVec(12f, 56f), rowWidth, "Kodie is drawing!", "K", new Vector4(0.67f, 0.45f, 1f, 1f), false, false);
            DrawPictionaryChatRow(drawList, origin + UiVec(12f, 93f), rowWidth, "Mattie: Cat?", "M", new Vector4(0.42f, 0.65f, 1f, 1f), false, false);
            DrawPictionaryChatRow(drawList, origin + UiVec(12f, 130f), rowWidth, "Mattie guessed correctly!", "", Good, true, false);
            DrawPictionaryChatRow(drawList, origin + UiVec(12f, 167f), rowWidth, "Luna: Lion?", "L", new Vector4(0.98f, 0.42f, 0.66f, 1f), false, false);
            DrawPictionaryChatRow(drawList, origin + UiVec(12f, 204f), rowWidth, "30 seconds remaining", "", new Vector4(0.42f, 0.62f, 0.92f, 1f), false, true);

            var inputMin = origin + new Vector2(Ui(12f), height - Ui(52f));
            var sendSize = Ui(40f);
            var inputMax = origin + new Vector2(width - Ui(20f) - sendSize, height - Ui(12f));
            drawList.AddRectFilled(inputMin, inputMax, ImGui.GetColorU32(new Vector4(0.05f, 0.06f, 0.11f, 1f)), Ui(8f));
            drawList.AddRect(inputMin, inputMax, ImGui.GetColorU32(AccentHover), Ui(8f), ImDrawFlags.None, Ui(1.5f));
            drawList.AddText(inputMin + UiVec(13f, 11f), ImGui.GetColorU32(MutedText), "Type a guess...");
            var sendMin = new Vector2(inputMax.X + Ui(8f), inputMin.Y);
            drawList.AddRectFilled(sendMin, sendMin + new Vector2(sendSize, Ui(40f)), ImGui.GetColorU32(Accent), Ui(8f));
            DrawPictionaryIcon(drawList, FontAwesomeIcon.PaperPlane, sendMin + UiVec(11f, 11f), 1f, Vector4.One);
            ImGui.Dummy(new Vector2(width, height));
        }
        ImGui.EndChild();
    }

    private void DrawPictionaryChatRow(ImDrawListPtr drawList, Vector2 min, float width, string text,
        string initial, Vector4 colour, bool success, bool system, bool hasReply = false,
        bool highlighted = false, float? expandedHeight = null)
    {
        var rowHeight = expandedHeight ?? Ui(32f);
        var max = min + new Vector2(width, rowHeight);
        var middleY = rowHeight * 0.5f;
        var fill = success
            ? new Vector4(0.03f, 0.24f, 0.16f, 0.82f)
            : new Vector4(0.08f, 0.11f, 0.18f, 1f);
        drawList.AddRectFilled(min, max, ImGui.GetColorU32(fill), Ui(7f));
        drawList.AddRect(min, max, ImGui.GetColorU32(success ? Good : new Vector4(BorderSubtle.X, BorderSubtle.Y, BorderSubtle.Z, 0.65f)), Ui(7f));
        if (highlighted)
            drawList.AddRect(min - UiVec(2f, 2f), max + UiVec(2f, 2f),
                ImGui.GetColorU32(AccentHover), Ui(8f), ImDrawFlags.None, Ui(2f));
        if (success)
        {
            drawList.AddCircleFilled(min + new Vector2(Ui(17f), middleY), Ui(9f), ImGui.GetColorU32(Good));
            DrawPictionaryIcon(drawList, FontAwesomeIcon.Check, min + new Vector2(Ui(12f), middleY - Ui(5f)), 0.72f, new Vector4(0.02f, 0.12f, 0.08f, 1f));
        }
        else if (system)
        {
            DrawPictionaryIcon(drawList, FontAwesomeIcon.Clock, min + new Vector2(Ui(9f), middleY - Ui(8f)), 0.82f, colour);
        }
        else
        {
            DrawPictionaryAvatar(drawList, min + new Vector2(Ui(17f), middleY), Ui(10f), colour, initial);
        }
        var textMin = min + UiVec(36f, 0f);
        var textMax = new Vector2(max.X - Ui(hasReply ? 34f : 8f), max.Y);
        var lines = WrapPictionaryChatText(text, textMax.X - textMin.X, expandedHeight.HasValue ? 2 : 1);
        var lineHeight = ImGui.GetTextLineHeight() + Ui(2f);
        var textY = min.Y + (rowHeight - (lines.Count * lineHeight - Ui(2f))) * 0.5f;
        var textColour = ImGui.GetColorU32(success ? new Vector4(0.35f, 1f, 0.65f, 1f) : system ? MutedText : new Vector4(0.83f, 0.86f, 0.95f, 1f));
        drawList.PushClipRect(textMin, textMax, true);
        foreach (var line in lines)
        {
            drawList.AddText(new Vector2(textMin.X, textY), textColour, line);
            textY += lineHeight;
        }
        drawList.PopClipRect();
    }

    private static List<string> WrapPictionaryChatText(string text, float maxWidth, int maxLines)
    {
        var normalized = text.Replace('\r', ' ').Replace('\n', ' ');
        var elements = new List<string>();
        var enumerator = System.Globalization.StringInfo.GetTextElementEnumerator(normalized);
        while (enumerator.MoveNext())
            elements.Add(enumerator.GetTextElement());

        var lines = new List<string>(maxLines);
        var position = 0;
        while (position < elements.Count && lines.Count < maxLines)
        {
            while (position < elements.Count && string.IsNullOrWhiteSpace(elements[position]))
                position++;
            var start = position;
            var lastSpace = -1;
            var line = string.Empty;
            while (position < elements.Count)
            {
                var candidate = line + elements[position];
                if (line.Length > 0 && ImGui.CalcTextSize(candidate).X > maxWidth)
                    break;
                line = candidate;
                if (string.IsNullOrWhiteSpace(elements[position]))
                    lastSpace = position;
                position++;
            }

            if (position < elements.Count && lastSpace > start)
            {
                position = lastSpace + 1;
                line = string.Concat(elements.Skip(start).Take(lastSpace - start)).TrimEnd();
            }

            while (position < elements.Count && string.IsNullOrWhiteSpace(elements[position]))
                position++;
            if (position < elements.Count && lines.Count == maxLines - 1)
            {
                while (line.Length > 0 && ImGui.CalcTextSize(line + "...").X > maxWidth)
                {
                    var end = System.Globalization.StringInfo.ParseCombiningCharacters(line);
                    line = line[..end[^1]];
                }
                line = line.TrimEnd() + "...";
            }
            lines.Add(line);
        }
        if (lines.Count == 0)
            lines.Add(string.Empty);
        return lines;
    }

    private void DrawPictionaryFooter(bool teams = false)
    {
        using var colours = ImRaii.PushColor(ImGuiCol.ChildBg, CardBg).Push(ImGuiCol.Border, BorderSubtle);
        using var style = ImRaii.PushStyle(ImGuiStyleVar.ChildRounding, Ui(9f))
            .Push(ImGuiStyleVar.ChildBorderSize, Ui(1f))
            .Push(ImGuiStyleVar.WindowPadding, UiVec(18f, 13f));
        if (UiFonts.Window(ImGui.GetFontSize(), ImGui.BeginChild("##pictionaryFooter", new Vector2(ImGui.GetContentRegionAvail().X, Ui(54f)), true, ImGuiWindowFlags.NoScrollbar)))
        {
            var origin = ImGui.GetCursorScreenPos();
            DrawPictionaryIcon(ImGui.GetWindowDrawList(), FontAwesomeIcon.Tv, origin + UiVec(0f, 1f), 1.15f, new Vector4(0.72f, 0.76f, 0.90f, 1f));
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + Ui(36f));
            ImGui.SetCursorPosY(ImGui.GetCursorPosY() + Ui(3f));
            if ((teams ? CurrentTeamsDrawerNotification : CurrentPictionaryDrawerNotification) is { } notice)
            {
                var textOrigin = ImGui.GetCursorScreenPos();
                var textSize = ImGui.GetContentRegionAvail();
                var drawList = ImGui.GetWindowDrawList();
                drawList.PushClipRect(textOrigin, textOrigin + textSize, true);
                drawList.AddText(textOrigin, ImGui.GetColorU32(notice.Correct ? Good : Danger), notice.Text);
                drawList.PopClipRect();
                ImGui.Dummy(new Vector2(textSize.X, ImGui.GetTextLineHeight()));
                if (ImGui.IsItemHovered()) UiFonts.SetTooltip(notice.Text);
            }
            else ImGui.TextColored(MutedText, "The drawing appears live on every connected TV.");
        }
        ImGui.EndChild();
    }

    private static void DrawPictionaryPill(ImDrawListPtr drawList, Vector2 min, string label, Vector4 colour, float width)
    {
        var size = new Vector2(width, Ui(38f));
        drawList.AddRectFilled(min, min + size, ImGui.GetColorU32(colour), size.Y * 0.5f);
        var textSize = ImGui.CalcTextSize(label);
        drawList.AddText(min + (size - textSize) * 0.5f, ImGui.GetColorU32(Vector4.One), label);
    }

    private static void DrawPictionaryAvatar(ImDrawListPtr drawList, Vector2 center, float radius, Vector4 colour, string initial)
    {
        drawList.AddCircleFilled(center, radius, ImGui.GetColorU32(new Vector4(0.06f, 0.08f, 0.13f, 1f)));
        drawList.AddCircle(center, radius, ImGui.GetColorU32(colour), 24, Ui(2f));
        DrawAvatarIcon(drawList, center, radius * 2f);
    }

    private static void DrawPictionaryIcon(ImDrawListPtr drawList, FontAwesomeIcon icon, Vector2 position, float scale, Vector4 colour)
    {
        UiFonts.DrawIcon(drawList, ImGui.GetFontSize() * scale, position, ImGui.GetColorU32(colour), icon.ToIconString());
    }
}

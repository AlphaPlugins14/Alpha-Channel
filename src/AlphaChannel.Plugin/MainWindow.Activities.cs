using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private const float ActivityLibraryCardHeight = 238f;

    private string activitiesSearch = string.Empty;
    private string activitiesCategory = "All categories";
    private bool activitiesSoloFilter;
    private bool activitiesTeamFilter;
    private bool activitiesWatchPartyFilter;
    private bool activitiesTvFilter;

    private HomePage lastActivitiesPage = HomePage.Arcade;
    private HomePage activityLibraryReturnPage = HomePage.Arcade;

    private void DrawActivitiesHero()
    {
        var origin = ImGui.GetCursorScreenPos();
        var available = ImGui.GetContentRegionAvail().X;
        using var uiFontScale6032=UiFonts.PushRelative(1.72f);
        ImGui.TextColored(Vector4.One, "Choose an activity");
        uiFontScale6032.Dispose();
        ImGui.TextColored(MutedText, "Find something to play together.");
        var afterHeading = ImGui.GetCursorScreenPos();
        var buttonWidth = Ui(138f);
        var gap = Ui(12f);
        var cardWidth = Math.Min(available, Ui(360f));
        Vector2 cardOrigin;
        Vector2 buttonOrigin;
        if (available >= Ui(790f))
        {
            cardOrigin = origin + new Vector2(available - cardWidth, 0f);
            buttonOrigin = cardOrigin - new Vector2(buttonWidth + gap, 0f);
        }
        else if (available >= Ui(520f))
        {
            cardWidth = Math.Min(cardWidth, available - buttonWidth - gap);
            buttonOrigin = afterHeading + UiVec(0f, 8f);
            cardOrigin = buttonOrigin + new Vector2(buttonWidth + gap, 0f);
        }
        else
        {
            buttonOrigin = afterHeading + UiVec(0f, 8f);
            cardOrigin = buttonOrigin + UiVec(0f, 82f);
        }
        DrawEndCurrentActivityButton(buttonOrigin, buttonWidth);
        DrawActivityPointsCard(cardOrigin, cardWidth);
        ImGui.SetCursorScreenPos(new Vector2(origin.X,
            Math.Max(afterHeading.Y, Math.Max(buttonOrigin.Y, cardOrigin.Y) + Ui(70f)) + Ui(8f)));
    }

    private void DrawEndCurrentActivityButton(Vector2 origin, float width)
    {
        var activity = ActiveActivity;
        var active = activity != ActiveActivityKind.None;
        var canEnd = active && IsLocalActivityHost;
        var size = new Vector2(width, Ui(70f));
        ImGui.SetCursorScreenPos(origin);
        var clicked = ImGui.InvisibleButton("##endCurrentActivity", size);
        var hovered = ImGui.IsItemHovered();
        var draw = ImGui.GetWindowDrawList();
        var fill = canEnd
            ? hovered ? new Vector4(0.64f, 0.12f, 0.18f, 1f) : new Vector4(0.43f, 0.07f, 0.12f, 1f)
            : new Vector4(0.20f, 0.15f, 0.19f, 0.64f);
        var border = canEnd
            ? new Vector4(0.96f, 0.31f, 0.37f, 1f)
            : new Vector4(0.46f, 0.35f, 0.40f, 0.55f);
        var textColour = canEnd ? Vector4.One : new Vector4(0.72f, 0.67f, 0.72f, 0.68f);
        draw.AddRectFilled(origin, origin + size, ImGui.GetColorU32(fill), Ui(9f));
        draw.AddRect(origin, origin + size, ImGui.GetColorU32(border), Ui(9f),
            ImDrawFlags.None, Ui(1.5f));
        var firstLine = "END CURRENT";
        var secondLine = "ACTIVITY";
        var firstSize = ImGui.CalcTextSize(firstLine);
        var secondSize = ImGui.CalcTextSize(secondLine);
        draw.AddText(origin + new Vector2((width - firstSize.X) * 0.5f, Ui(17f)),
            ImGui.GetColorU32(textColour), firstLine);
        draw.AddText(origin + new Vector2((width - secondSize.X) * 0.5f, Ui(36f)),
            ImGui.GetColorU32(textColour), secondLine);
        if (hovered)
        {
            if (!active) UiFonts.SetTooltip("No activity is currently active");
            else if (!canEnd) UiFonts.SetTooltip("Only the Watch Party host can end the current activity");
            else ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
        }
        if (clicked && canEnd) EndCurrentActivity(activity);
    }

    private void EndCurrentActivity(ActiveActivityKind activity)
    {
        if (!IsLocalActivityHost || ActiveActivity != activity) return;
        switch (activity)
        {
            case ActiveActivityKind.Whiteboard when whiteboardSessionId is not null:
                FinishWhiteboardStroke();
                SendAndApplyWhiteboardCommand($"X|{whiteboardSessionId}");
                break;
            case ActiveActivityKind.Pictionary:
                StopPictionaryGame();
                break;
            case ActiveActivityKind.PictionaryTeams:
                StopPictionaryTeams();
                break;
            case ActiveActivityKind.Quiz:
                StopQuiz();
                break;
            case ActiveActivityKind.Roulette:
                StopRouletteGame();
                break;
            case ActiveActivityKind.Bingo:
                StopBingoGame();
                break;
            case ActiveActivityKind.BlastParty:
                StopBlastParty();
                break;
            case ActiveActivityKind.SkyRail:
                StopSkyRailGame();
                break;
            case ActiveActivityKind.Pixels:
                StopPixels();
                break;
            case ActiveActivityKind.LuckyWheel:
                StopLuckyWheel();
                break;
            case ActiveActivityKind.SlotMachine:
                StopSlotMachineGame();
                break;
            case ActiveActivityKind.Poker:
                StopPokerTable();
                break;
            case ActiveActivityKind.ChocoRaceBets:
                StopRacePreview();
                break;
        }
    }

    private void DrawActivitiesToolbar()
    {
        var available = ImGui.GetContentRegionAvail().X;
        var wrap = available < Ui(900f);
        var categoryWidth = Ui(150f);
        var gap = Ui(7f);
        var filterWidth = Ui(55f + 62f + 68f + 112f + 132f) + gap * 4f;
        var searchWidth = wrap
            ? Math.Max(Ui(190f), available - categoryWidth - gap)
            : Math.Max(Ui(210f), available - categoryWidth - filterWidth - gap * 2f);

        using var colours = ImRaii.PushColor(ImGuiCol.FrameBg, CardBg)
            .Push(ImGuiCol.FrameBgHovered, CardBgHover)
            .Push(ImGuiCol.FrameBgActive, CardBgHover)
            .Push(ImGuiCol.Border, BorderSubtle);
        using var style = ImRaii.PushStyle(ImGuiStyleVar.FrameRounding, Ui(9f))
            .Push(ImGuiStyleVar.FrameBorderSize, Ui(1f))
            .Push(ImGuiStyleVar.FramePadding, UiVec(12f, 10f));

        ImGui.SetNextItemWidth(searchWidth);
        ImGui.InputTextWithHint("##activitiesSearch", "Search activities...", ref activitiesSearch, 100);

        if (wrap)
        {
            ImGui.SameLine(0f, gap);
            DrawActivitiesCategoryCombo(categoryWidth);
            ImGui.Dummy(UiVec(0f, 1f));
            DrawActivitiesFilterRow(gap);
        }
        else
        {
            ImGui.SameLine(0f, gap);
            DrawActivitiesFilterRow(gap);
            ImGui.SameLine(0f, gap);
            DrawActivitiesCategoryCombo(categoryWidth);
        }

        ImGui.Dummy(UiVec(0f, 3f));
    }

    private void DrawActivitiesFilterRow(float gap)
    {
        var allSelected = !activitiesSoloFilter && !activitiesTeamFilter &&
                          !activitiesWatchPartyFilter && !activitiesTvFilter &&
                          activitiesCategory == "All categories";
        if (DrawActivitiesFilterPill("All", allSelected, Ui(55f)))
        {
            activitiesSoloFilter = false;
            activitiesTeamFilter = false;
            activitiesWatchPartyFilter = false;
            activitiesTvFilter = false;
            activitiesCategory = "All categories";
        }

        ImGui.SameLine(0f, gap);
        if (DrawActivitiesFilterPill("Solo", activitiesSoloFilter, Ui(62f)))
            activitiesSoloFilter = !activitiesSoloFilter;
        ImGui.SameLine(0f, gap);
        if (DrawActivitiesFilterPill("Team", activitiesTeamFilter, Ui(68f)))
            activitiesTeamFilter = !activitiesTeamFilter;
        ImGui.SameLine(0f, gap);
        if (DrawActivitiesFilterPill("Watch Party", activitiesWatchPartyFilter, Ui(112f)))
            activitiesWatchPartyFilter = !activitiesWatchPartyFilter;
        ImGui.SameLine(0f, gap);
        if (DrawActivitiesFilterPill("Playable on TV", activitiesTvFilter, Ui(132f)))
            activitiesTvFilter = !activitiesTvFilter;
    }

    private bool DrawActivitiesFilterPill(string label, bool selected, float width)
    {
        var size = new Vector2(width, Ui(40f));
        var origin = ImGui.GetCursorScreenPos();
        ImGui.PushID("activityFilter" + label);
        var clicked = ImGui.InvisibleButton("##pill", size);
        var hovered = ImGui.IsItemHovered();
        ImGui.PopID();
        var fill = selected
            ? Accent
            : hovered ? CardBgHover : new Vector4(0.035f, 0.045f, 0.08f, 1f);
        var border = selected ? AccentHover : BorderSubtle;
        var drawList = ImGui.GetWindowDrawList();
        drawList.AddRectFilled(origin, origin + size, ImGui.GetColorU32(fill), size.Y * 0.5f);
        drawList.AddRect(origin, origin + size, ImGui.GetColorU32(border), size.Y * 0.5f);
        var textSize = ImGui.CalcTextSize(label);
        drawList.AddText(origin + (size - textSize) * 0.5f, ImGui.GetColorU32(Vector4.One), label);
        return clicked;
    }

    private void DrawActivitiesCategoryCombo(float width)
    {
        ImGui.SetNextItemWidth(width);
        if (!UiFonts.Window(ImGui.GetFontSize(), ImGui.BeginCombo("##activitiesCategory", activitiesCategory)))
            return;
        foreach (var category in ActivityCategories)
        {
            var selected = activitiesCategory == category;
            if (ImGui.Selectable(category, selected))
                activitiesCategory = category;
            if (selected)
                ImGui.SetItemDefaultFocus();
        }
        ImGui.EndCombo();
    }

    private bool ActivityMatchesFilters(ActivityLibraryEntry activity)
    {
        if (!string.IsNullOrWhiteSpace(activitiesSearch) &&
            !activity.Title.Contains(activitiesSearch, StringComparison.OrdinalIgnoreCase) &&
            !activity.Description.Contains(activitiesSearch, StringComparison.OrdinalIgnoreCase) &&
            !activity.Categories.Any(category => category.Contains(activitiesSearch, StringComparison.OrdinalIgnoreCase)))
            return false;
        if (activitiesCategory != "All categories" && !activity.Categories.Contains(activitiesCategory))
            return false;
        if (activitiesSoloFilter && !activity.SupportsSolo)
            return false;
        if (activitiesTeamFilter && !activity.SupportsTeam)
            return false;
        if (activitiesWatchPartyFilter && !activity.SupportsWatchParty)
            return false;
        return !activitiesTvFilter || activity.PlayableOnTv;
    }

    private void DrawActivityLibraryCard(ActivityLibraryEntry activity, float width)
    {
        var height = Ui(ActivityLibraryCardHeight);
        var size = new Vector2(width, height);
        var origin = ImGui.GetCursorScreenPos();
        ImGui.PushID("activityLibrary" + activity.Id);
        var clicked = ImGui.InvisibleButton("##card", size);
        var hovered = ImGui.IsItemHovered();
        ImGui.PopID();

        var cardActivity = activity.Id switch
        {
            "whiteboard" => ActiveActivityKind.Whiteboard,
            "pictionary" => ActiveActivityKind.Pictionary,
            "quiz" => ActiveActivityKind.Quiz,
            "roulettePrototype" => ActiveActivityKind.Roulette,
            "bingo" => ActiveActivityKind.Bingo,
            "blastParty" => ActiveActivityKind.BlastParty,
            "skyRail" => ActiveActivityKind.SkyRail,
            "pixels" => ActiveActivityKind.Pixels,
            "luckyWheel" => ActiveActivityKind.LuckyWheel,
            "slotMachine" => ActiveActivityKind.SlotMachine,
            "poker" => ActiveActivityKind.Poker,
            "racePreview" => ActiveActivityKind.ChocoRaceBets,
            _ => ActiveActivityKind.None,
        };
        var active = cardActivity != ActiveActivityKind.None && ActiveActivity == cardActivity;
        var blocked = activity.Enabled && cardActivity != ActiveActivityKind.None &&
            !CanLaunchActivity(cardActivity);
        var drawList = ImGui.GetWindowDrawList();
        var cardFill = activity.Enabled && hovered ? CardBgHover : CardBg;
        if (!activity.Enabled)
            cardFill = new Vector4(CardBg.X, CardBg.Y, CardBg.Z, 0.78f);
        var cardBorder = active ? Good : activity.Enabled && hovered ? Accent : BorderSubtle;
        drawList.AddRectFilled(origin, origin + size, ImGui.GetColorU32(cardFill), Ui(10f));
        drawList.AddRect(origin, origin + size, ImGui.GetColorU32(cardBorder), Ui(10f), ImDrawFlags.None, active ? Ui(2f) : Ui(1f));

        var badgeX = origin.X + Ui(14f);
        foreach (var category in activity.Categories)
        {
            var label = category.ToUpperInvariant();
            DrawActivityLibraryBadge(drawList, new Vector2(badgeX, origin.Y + Ui(12f)), label, activity.Enabled);
            badgeX += ImGui.CalcTextSize(label).X + Ui(30f);
        }
        if (active)
        {
            var activeText = "IN PROGRESS";
            var activeSize = ImGui.CalcTextSize(activeText);
            var activeX = origin.X + size.X - activeSize.X - Ui(14f);
            if (activeX >= badgeX)
                drawList.AddText(new Vector2(activeX, origin.Y + Ui(17f)), ImGui.GetColorU32(Good), activeText);
            else
                drawList.AddCircleFilled(origin + new Vector2(size.X - Ui(18f), Ui(24f)), Ui(5f), ImGui.GetColorU32(Good));
        }

        ImGui.SetCursorScreenPos(origin + UiVec(14f, 45f));
        DrawActivityPreview(activity.Id + "Library", activity.Preview, width - Ui(28f), 98f, 9f, 7f, false);

        var titlePos = origin + UiVec(15f, 153f);
        UiFonts.DrawText(drawList, ImGui.GetFontSize() * 1.22f, titlePos, ImGui.GetColorU32(activity.Enabled ? Vector4.One : MutedText), activity.Title);
        var descriptionPos = origin + UiVec(15f, 181f);
        drawList.PushClipRect(descriptionPos, origin + new Vector2(size.X - Ui(14f), Ui(207f)), true);
        drawList.AddText(descriptionPos, ImGui.GetColorU32(MutedText), activity.Description);
        drawList.PopClipRect();

        var footerY = origin.Y + Ui(215f);
        var playerIcon = activity.SupportsTeam ? FontAwesomeIcon.Users : FontAwesomeIcon.User;
        UiFonts.DrawIcon(drawList, ImGui.GetFontSize(), new Vector2(origin.X + Ui(15f), footerY), ImGui.GetColorU32(AccentHover), playerIcon.ToIconString());
        drawList.AddText(new Vector2(origin.X + Ui(38f), footerY), ImGui.GetColorU32(MutedText), activity.PlayerLabel);
        if (activity.PlayableOnTv)
        {
            var tv = FontAwesomeIcon.Tv.ToIconString();
            UiFonts.DrawIcon(drawList, ImGui.GetFontSize(), new Vector2(origin.X + size.X - Ui(33f), footerY), ImGui.GetColorU32(AccentHover), tv);
        }

        if (hovered)
        {
            var modes = activity.SupportsSolo && activity.SupportsTeam ? "Solo or team" : activity.SupportsTeam ? "Team" : "Solo";
            UiFonts.SetTooltip(blocked
                ? "End current activity to launch another"
                : activity.Enabled
                ? $"{activity.Title}\n{modes} • {activity.PlayerLabel}\n{(activity.RequiresWatchParty ? "Watch Party required" : "Watch Party optional")}\n{(activity.PlayableOnTv ? "Playable on the TV" : "Plugin window only")}" 
                : $"{activity.Title}\nComing soon");
        }

        if (clicked && activity.Enabled && !blocked)
            OpenActivityLibraryEntry(activity);

        ImGui.SetCursorScreenPos(origin + new Vector2(0f, height));
    }

    private static void DrawActivityLibraryBadge(ImDrawListPtr drawList, Vector2 origin, string label, bool enabled)
    {
        var textSize = ImGui.CalcTextSize(label);
        var size = textSize + UiVec(24f, 12f);
        var fill = enabled ? AccentActive : new Vector4(0.20f, 0.22f, 0.34f, 1f);
        drawList.AddRectFilled(origin, origin + size, ImGui.GetColorU32(fill), size.Y * 0.5f);
        drawList.AddText(origin + (size - textSize) * 0.5f, ImGui.GetColorU32(Vector4.One), label);
    }

    private void OpenActivityLibraryEntry(ActivityLibraryEntry activity)
    {
        if (activity.Id == "pixels")
            OpenPixelsPage();
        else if (activity.Id == "racePreview")
            OpenRacePreviewPage();
        else if (activity.Id == "roulettePrototype")
            OpenRoulettePrototypePage();
        else if (activity.Id == "bingo")
            OpenBingoPage();
        else if (activity.Id == "skyRail")
            OpenSkyRailPage();
        else if (activity.Id == "luckyWheel")
            OpenLuckyWheelPage();
        else if (activity.Id == "slotMachine")
            OpenSlotMachinePage();
        else if (activity.Id == "poker")
            OpenPokerPage();
        else if (activity.Destination == HomePage.Pictionary)
            OpenPictionarySetup();
        else if (activity.Destination == HomePage.PictionaryTeams)
            OpenPictionaryTeams();
        else if (activity.Destination is { } destination)
        {
            lastActivitiesPage = destination;
            currentPage = destination;
        }
    }

    private void DrawActivitiesEmptyState()
    {
        var width = ImGui.GetContentRegionAvail().X;
        using var colours = ImRaii.PushColor(ImGuiCol.ChildBg, CardBg).Push(ImGuiCol.Border, BorderSubtle);
        using var style = ImRaii.PushStyle(ImGuiStyleVar.ChildRounding, Ui(10f))
            .Push(ImGuiStyleVar.ChildBorderSize, Ui(1f)).Push(ImGuiStyleVar.WindowPadding, UiVec(20f, 20f));
        if (UiFonts.Window(ImGui.GetFontSize(), ImGui.BeginChild("##activitiesEmpty", new Vector2(width, Ui(120f)), true, ImGuiWindowFlags.NoScrollbar)))
        {
            using var uiFontScale23521=UiFonts.PushRelative(1.25f);
            ImGui.TextColored(Vector4.One, "No activities match those filters");
            uiFontScale23521.Dispose();
            ImGui.TextColored(MutedText, "Try another search or clear the selected filters.");
            if (ImGui.Button("Clear filters##activitiesEmptyClear", UiVec(130f, 34f)))
            {
                activitiesSearch = string.Empty;
                activitiesCategory = "All categories";
                activitiesSoloFilter = activitiesTeamFilter = activitiesWatchPartyFilter = activitiesTvFilter = false;
            }
        }
        ImGui.EndChild();
    }

    private void DrawActivityPreview(
        string id,
        ActivityPreviewKind preview,
        float width,
        float height = 70f,
        float horizontalInset = 30f,
        float verticalInset = 7f,
        bool registerItem = true,
        Vector4? background = null,
        bool insetBorder = true,
        bool gallery = false)
    {
        var size = new Vector2(width, Ui(height));
        var origin = ImGui.GetCursorScreenPos();
        if (registerItem)
            ImGui.InvisibleButton($"##activityPreview{id}", size);
        var drawList = ImGui.GetWindowDrawList();
        drawList.AddRectFilled(origin, origin + size,
            ImGui.GetColorU32(background ?? new Vector4(0.02f, 0.025f, 0.04f, 1f)), Ui(8f));
        drawList.AddRect(origin, origin + size, ImGui.GetColorU32(BorderSubtle), Ui(8f));
        drawList.PushClipRect(origin + UiVec(2f, 2f), origin + size - UiVec(2f, 2f), true);

        if (gallery && DrawArcadePreviewArtwork(drawList, preview, origin + UiVec(2f, 2f), size - UiVec(4f, 4f)))
        {
            drawList.PopClipRect();
            drawList.AddRect(origin + UiVec(4f, 4f), origin + size - UiVec(4f, 4f),
                ImGui.GetColorU32(HubTint(0x3C506C)), Ui(4f), ImDrawFlags.None, Ui(1f));
            drawList.AddRect(origin, origin + size, ImGui.GetColorU32(ArcadeCardBorder), Ui(7f));
            return;
        }

        var panelMin = origin + UiVec(horizontalInset, verticalInset);
        var panelMax = origin + new Vector2(size.X - Ui(horizontalInset), size.Y - Ui(verticalInset));
        if (insetBorder)
            drawList.AddRect(panelMin, panelMax, ImGui.GetColorU32(new Vector4(0.32f, 0.37f, 0.52f, 0.65f)), Ui(5f), ImDrawFlags.None, Ui(2f));
        var panelSize = panelMax - panelMin;
        Vector2 P(float x, float y) => panelMin + new Vector2(panelSize.X * x, panelSize.Y * y);

        if (preview == ActivityPreviewKind.Whiteboard)
        {
            drawList.AddBezierCubic(P(0.08f, 0.40f), P(0.22f, 0.04f), P(0.35f, 0.72f), P(0.48f, 0.34f), ImGui.GetColorU32(new Vector4(0.10f, 0.84f, 0.94f, 1f)), Ui(3f));
            Vector2[] zig = [P(0.10f, 0.78f), P(0.22f, 0.55f), P(0.19f, 0.84f), P(0.34f, 0.59f), P(0.30f, 0.84f), P(0.45f, 0.68f)];
            for (var index = 1; index < zig.Length; index++)
            {
                drawList.AddLine(zig[index - 1], zig[index], ImGui.GetColorU32(new Vector4(1f, 0.80f, 0.10f, 1f)), Ui(3f));
            }
            DrawActivitiesHeart(drawList, P(0.62f, 0.31f), panelSize * new Vector2(0.08f, 0.34f), ImGui.GetColorU32(new Vector4(0.98f, 0.35f, 0.67f, 1f)), Ui(3f));
            DrawActivitiesFace(drawList, P(0.84f, 0.55f), Math.Min(panelSize.X * 0.085f, panelSize.Y * 0.22f), ImGui.GetColorU32(Vector4.One), Ui(3f));
        }
        else if (preview == ActivityPreviewKind.Pixels)
        {
            for (var py = 0; py < 9; py++) for (var px = 0; px < 28; px++)
            {
                var rgb = PixelsBoard.Palette[(px * 7 + py * 11) % PixelsBoard.Palette.Length];
                var color = Hex((int)rgb);
                drawList.AddRectFilled(P(px/28f,py/9f),P((px+1)/28f,(py+1)/9f)-Vector2.One,ImGui.GetColorU32(color));
            }
        }
        else if (preview == ActivityPreviewKind.Pictionary)
        {
            var yellow = ImGui.GetColorU32(new Vector4(1f, 0.80f, 0.10f, 1f));
            var compact = size.X < Ui(180f);
            var center = P(0.48f, compact ? 0.65f : 0.58f);
            var radius = Math.Min(panelSize.X * 0.11f, panelSize.Y * (compact ? 0.19f : 0.26f));
            drawList.AddCircle(center, radius, yellow, 28, Ui(3f));
            drawList.AddTriangle(center + new Vector2(-radius * 0.82f, -radius * 0.55f), center + new Vector2(-radius * 0.52f, -radius * 1.25f), center + new Vector2(-radius * 0.12f, -radius * 0.78f), yellow, Ui(3f));
            drawList.AddTriangle(center + new Vector2(radius * 0.12f, -radius * 0.78f), center + new Vector2(radius * 0.52f, -radius * 1.25f), center + new Vector2(radius * 0.82f, -radius * 0.55f), yellow, Ui(3f));
            drawList.AddCircleFilled(center + new Vector2(-radius * 0.32f, -radius * 0.10f), Ui(2.3f), yellow);
            drawList.AddCircleFilled(center + new Vector2(radius * 0.32f, -radius * 0.10f), Ui(2.3f), yellow);
            drawList.AddLine(center + new Vector2(-radius * 0.12f, radius * 0.18f), center + new Vector2(radius * 0.12f, radius * 0.18f), yellow, Ui(2f));
            if (compact)
            {
                var bubbleWidth = MathF.Min(Ui(50f), (size.X - Ui(24f)) * 0.5f);
                var first = origin + UiVec(8f, 8f);
                var second = new Vector2(origin.X + size.X - bubbleWidth - Ui(8f), first.Y);
                drawList.AddRectFilled(first, first + new Vector2(bubbleWidth, Ui(21f)),
                    ImGui.GetColorU32(new Vector4(0.19f, 0.22f, 0.32f, 1f)), Ui(10f));
                drawList.AddRectFilled(second, second + new Vector2(bubbleWidth, Ui(21f)),
                    ImGui.GetColorU32(new Vector4(Accent.X, Accent.Y, Accent.Z, 0.85f)), Ui(10f));
                drawList.AddText(first + UiVec(9f, 2f), ImGui.GetColorU32(Vector4.One), "Cat?");
                drawList.AddText(second + UiVec(9f, 2f), ImGui.GetColorU32(Vector4.One), "Cat!");
            }
            else
            {
                var bubbleMin = origin + new Vector2(size.X - Ui(98f), Ui(8f));
                drawList.AddRectFilled(bubbleMin, bubbleMin + UiVec(66f, 24f), ImGui.GetColorU32(new Vector4(0.19f, 0.22f, 0.32f, 1f)), Ui(12f));
                drawList.AddText(bubbleMin + UiVec(14f, 4f), ImGui.GetColorU32(Vector4.One), "Cat?");
                drawList.AddRectFilled(bubbleMin + UiVec(0f, 32f), bubbleMin + UiVec(66f, 56f), ImGui.GetColorU32(new Vector4(Accent.X, Accent.Y, Accent.Z, 0.85f)), Ui(12f));
                drawList.AddText(bubbleMin + UiVec(14f, 36f), ImGui.GetColorU32(Vector4.One), "Cat!");
            }
        }
        else if (preview == ActivityPreviewKind.PictionaryTeams)
        {
            var red = ImGui.GetColorU32(new Vector4(1f, 0.39f, 0.46f, 1f));
            var blue = ImGui.GetColorU32(new Vector4(0.34f, 0.78f, 1f, 1f));
            var ink = ImGui.GetColorU32(Vector4.One);
            drawList.AddRectFilled(P(0.01f, 0.02f), P(0.49f, 0.98f),
                ImGui.GetColorU32(new Vector4(0.20f, 0.06f, 0.09f, 0.45f)));
            drawList.AddRectFilled(P(0.51f, 0.02f), P(0.99f, 0.98f),
                ImGui.GetColorU32(new Vector4(0.05f, 0.14f, 0.22f, 0.55f)));
            drawList.AddLine(P(0.50f, 0.07f), P(0.50f, 0.93f), ink, Ui(2f));

            // Two separate sketches make the split-screen rules clear at card size.
            var cat = P(0.25f, 0.57f);
            var catRadius = Math.Min(panelSize.X * 0.105f, panelSize.Y * 0.23f);
            drawList.AddCircle(cat, catRadius, red, 24, Ui(2.5f));
            drawList.AddTriangle(cat + new Vector2(-catRadius * 0.85f, -catRadius * 0.55f),
                cat + new Vector2(-catRadius * 0.60f, -catRadius * 1.35f),
                cat + new Vector2(-catRadius * 0.15f, -catRadius * 0.94f), red, Ui(2.5f));
            drawList.AddTriangle(cat + new Vector2(catRadius * 0.15f, -catRadius * 0.94f),
                cat + new Vector2(catRadius * 0.60f, -catRadius * 1.35f),
                cat + new Vector2(catRadius * 0.85f, -catRadius * 0.55f), red, Ui(2.5f));
            drawList.AddCircleFilled(cat + new Vector2(-catRadius * 0.32f, -catRadius * 0.10f), Ui(1.8f), red);
            drawList.AddCircleFilled(cat + new Vector2(catRadius * 0.32f, -catRadius * 0.10f), Ui(1.8f), red);
            drawList.AddLine(cat + new Vector2(-catRadius * 0.15f, catRadius * 0.28f),
                cat + new Vector2(catRadius * 0.15f, catRadius * 0.28f), red, Ui(2f));

            var bird = P(0.75f, 0.55f);
            var birdRadius = Math.Min(panelSize.X * 0.10f, panelSize.Y * 0.22f);
            drawList.AddCircle(bird, birdRadius, blue, 24, Ui(2.5f));
            drawList.AddLine(bird + new Vector2(-birdRadius * 0.35f, -birdRadius * 0.90f),
                bird + new Vector2(-birdRadius * 0.08f, -birdRadius * 1.42f), blue, Ui(2.5f));
            drawList.AddLine(bird + new Vector2(0f, -birdRadius),
                bird + new Vector2(birdRadius * 0.38f, -birdRadius * 1.42f), blue, Ui(2.5f));
            drawList.AddCircleFilled(bird + new Vector2(-birdRadius * 0.38f, -birdRadius * 0.08f), Ui(1.8f), blue);
            drawList.AddCircleFilled(bird + new Vector2(birdRadius * 0.38f, -birdRadius * 0.08f), Ui(1.8f), blue);
            drawList.AddTriangle(bird + new Vector2(-birdRadius * 0.20f, birdRadius * 0.25f),
                bird + new Vector2(birdRadius * 0.20f, birdRadius * 0.25f),
                bird + new Vector2(0f, birdRadius * 0.62f), blue, Ui(2f));
        }
        else if (preview == ActivityPreviewKind.Quiz)
        {
            var purple = ImGui.GetColorU32(new Vector4(0.58f, 0.36f, 0.92f, 1f));
            var cyan = ImGui.GetColorU32(new Vector4(0.20f, 0.72f, 1f, 1f));
            var pink = ImGui.GetColorU32(new Vector4(0.98f, 0.35f, 0.67f, 1f));
            var yellow = ImGui.GetColorU32(new Vector4(1f, 0.80f, 0.10f, 1f));
            var optionColours = new[] { purple, cyan, pink, yellow };
            for (var index = 0; index < optionColours.Length; index++)
            {
                var column = index % 2;
                var row = index / 2;
                var min = P(0.08f + column * 0.48f, 0.12f + row * 0.46f);
                var max = P(0.52f + column * 0.40f, 0.46f + row * 0.42f);
                drawList.AddRectFilled(min, max, ImGui.GetColorU32(new Vector4(0.08f, 0.10f, 0.18f, 1f)), Ui(4f));
                drawList.AddRect(min, max, optionColours[index], Ui(4f), ImDrawFlags.None, Ui(2f));
                drawList.AddCircleFilled(min + UiVec(12f, 9f), Ui(4f), optionColours[index]);
            }
        }
        else if (preview == ActivityPreviewKind.Roulette)
        {
            DrawRoulettePrototypeActivityPreview(drawList, panelMin, panelMax);
        }
        else if (preview == ActivityPreviewKind.RacePreview)
        {
            var colours = new[] { 0xFFF5C84Bu, 0xFF56BFEFu, 0xFFF27B99u, 0xFFF5F4EEu, 0xFFB478E9u, 0xFF474A55u };
            for (var index = 0; index < colours.Length; index++)
            {
                var y = 0.12f + index * 0.15f;
                drawList.AddLine(P(0.10f, y + 0.08f), P(0.90f, y + 0.08f), ImGui.GetColorU32(BorderSubtle), Ui(1f));
                drawList.AddCircleFilled(P(0.35f + index * 0.05f, y), panelSize.Y * 0.045f, colours[index]);
            }
            drawList.AddLine(P(0.91f, 0.10f), P(0.91f, 0.91f), ImGui.GetColorU32(Vector4.One), Ui(2f));
        }
        else if (preview == ActivityPreviewKind.SkyRail)
        {
            var blue = ImGui.GetColorU32(new Vector4(0.35f, 0.77f, 1f, 1f));
            var silver = ImGui.GetColorU32(new Vector4(0.87f, 0.91f, 1f, 1f));
            var gold = ImGui.GetColorU32(new Vector4(1f, 0.78f, 0.27f, 1f));
            var red = ImGui.GetColorU32(new Vector4(0.98f, 0.29f, 0.39f, 1f));
            var targets = new[] { (P(0.18f, 0.39f), blue), (P(0.40f, 0.67f), silver),
                (P(0.63f, 0.34f), gold), (P(0.84f, 0.67f), red) };
            foreach (var (center, colour) in targets)
            {
                drawList.AddCircle(center, panelSize.Y * 0.15f, colour, 24, Ui(3f));
                drawList.AddCircle(center, panelSize.Y * 0.085f, colour, 24, Ui(2f));
                drawList.AddCircleFilled(center, panelSize.Y * 0.028f, colour);
            }
        }
        else if (preview == ActivityPreviewKind.LuckyWheel)
        {
            var center = P(.5f, .53f);
            var radius = Math.Min(panelSize.X, panelSize.Y) * .38f;
            for (var i = 0; i < 7; i++)
            {
                var colour = WheelRgb(LuckyWheelRules.Rainbow[i]);
                var ink = ImGui.GetColorU32(new Vector4(colour, 1));
                for (var j = 0; j < 14; j++)
                {
                    var a = -MathF.PI / 2 + (i + j / 14f) * MathF.Tau / 7;
                    var b = -MathF.PI / 2 + (i + (j + 1) / 14f) * MathF.Tau / 7;
                    drawList.AddTriangleFilled(center, center + new Vector2(MathF.Cos(a), MathF.Sin(a)) * radius,
                        center + new Vector2(MathF.Cos(b), MathF.Sin(b)) * radius, ink);
                }
            }
            drawList.AddCircle(center, radius, ImGui.GetColorU32(AccentHover), 64, Ui(2));
            drawList.AddCircleFilled(center, radius * .12f, ImGui.GetColorU32(Vector4.One));
            drawList.AddTriangleFilled(center + new Vector2(-radius * .12f, -radius * 1.14f),
                center + new Vector2(radius * .12f, -radius * 1.14f), center + new Vector2(0, -radius * .8f), ImGui.GetColorU32(Vector4.One));
        }
        else if (preview == ActivityPreviewKind.SlotMachine)
        {
            for (var index = 0; index < 3; index++)
            {
                var left = P(0.09f + index * 0.30f, 0.30f);
                var right = P(0.35f + index * 0.30f, 0.70f);
                drawList.AddRectFilled(left, right,
                    ImGui.GetColorU32(new Vector4(0.95f, 0.90f, 0.80f, 1f)), Ui(4f));
                drawList.AddRect(left, right,
                    ImGui.GetColorU32(new Vector4(0.66f, 0.54f, 0.38f, 1f)), Ui(4f));
                // Draw three symbols from the playable reel set, not the old concept gallery.
                var sample = SlotMachineSamples[index switch
                {
                    0 => 0,
                    1 => 2,
                    _ => SlotMachineSamples.Length - 1,
                }];
                DrawSlotSymbol(drawList, sample - 1, (left + right) * 0.5f,
                    Math.Min(right.X - left.X, right.Y - left.Y) * 0.47f);
            }
        }
        else if (preview == ActivityPreviewKind.Poker)
        {
            DrawPokerActivityPreview(drawList, panelMin, panelMax);
        }
        else if (preview == ActivityPreviewKind.Bingo)
        {
            var colours = new[]
            {
                new Vector4(0.20f, 0.72f, 1f, 1f),
                new Vector4(0.98f, 0.35f, 0.67f, 1f),
                new Vector4(1f, 0.80f, 0.10f, 1f),
                new Vector4(0.35f, 0.86f, 0.42f, 1f),
                new Vector4(0.67f, 0.45f, 1f, 1f),
            };
            for (var index = 0; index < colours.Length; index++)
            {
                var center = P(0.18f + index * 0.16f, 0.24f);
                drawList.AddCircleFilled(center, Math.Min(panelSize.X, panelSize.Y) * 0.10f, ImGui.GetColorU32(colours[index]));
                var letter = "BINGO"[index].ToString();
                var letterSize = ImGui.CalcTextSize(letter);
                drawList.AddText(center - letterSize * 0.5f, ImGui.GetColorU32(new Vector4(0.03f, 0.04f, 0.07f, 1f)), letter);
            }
            for (var row = 0; row < 2; row++)
            for (var column = 0; column < 7; column++)
            {
                var min = P(0.13f + column * 0.11f, 0.50f + row * 0.25f);
                var max = min + new Vector2(panelSize.X * 0.075f, panelSize.Y * 0.17f);
                drawList.AddRect(min, max, ImGui.GetColorU32(new Vector4(0.32f, 0.37f, 0.52f, 0.85f)), Ui(2f));
                if ((row * 7 + column) % 4 == 1)
                    drawList.AddCircleFilled((min + max) * 0.5f, Math.Min(max.X - min.X, max.Y - min.Y) * 0.28f,
                        ImGui.GetColorU32(colours[(row + column) % colours.Length]));
            }
        }
        else
        {
            var purple = ImGui.GetColorU32(new Vector4(0.67f, 0.45f, 1f, 1f));
            var pale = ImGui.GetColorU32(new Vector4(0.80f, 0.65f, 1f, 1f));
            var star = FontAwesomeIcon.Star.ToIconString();
            var center = P(0.50f, 0.53f);
            UiFonts.DrawIcon(drawList, ImGui.GetFontSize() * 2.6f, center - UiVec(20f, 21f), purple, star);
            UiFonts.DrawIcon(drawList, ImGui.GetFontSize() * 0.72f, P(0.22f, 0.33f), pale, star);
            UiFonts.DrawIcon(drawList, ImGui.GetFontSize() * 0.72f, P(0.76f, 0.27f), pale, star);
            UiFonts.DrawIcon(drawList, ImGui.GetFontSize() * 0.58f, P(0.27f, 0.72f), purple, star);
            drawList.AddLine(P(0.30f, 0.68f), P(0.45f, 0.56f), purple, Ui(2f));
            drawList.AddLine(P(0.58f, 0.48f), P(0.75f, 0.31f), purple, Ui(2f));
        }
        drawList.PopClipRect();
        if (gallery)
            drawList.AddRect(origin + UiVec(4f, 4f), origin + size - UiVec(4f, 4f),
                ImGui.GetColorU32(HubTint(0x3C506C)), Ui(4f), ImDrawFlags.None, Ui(1f));
    }

    private void DrawActivityMetaPill(string id, string label, FontAwesomeIcon icon)
    {
        var iconText = icon.ToIconString();
        var textSize = ImGui.CalcTextSize(label);
        var width = textSize.X + Ui(40f);
        var size = new Vector2(width, Ui(30f));
        var origin = ImGui.GetCursorScreenPos();
        ImGui.InvisibleButton($"##{id}", size);
        var drawList = ImGui.GetWindowDrawList();
        drawList.AddRectFilled(origin, origin + size, ImGui.GetColorU32(CardBg), size.Y * 0.5f);
        drawList.AddRect(origin, origin + size, ImGui.GetColorU32(new Vector4(0.32f, 0.37f, 0.52f, 0.8f)), size.Y * 0.5f);
        UiFonts.DrawIcon(drawList, ImGui.GetFontSize() * 0.88f, origin + UiVec(10f, 7f), ImGui.GetColorU32(AccentHover), iconText);
        drawList.AddText(origin + UiVec(30f, 6.5f), ImGui.GetColorU32(new Vector4(0.89f, 0.90f, 0.97f, 1f)), label);
    }

    private void DrawActivityBadge(string label, bool primary)
    {
        var textSize = ImGui.CalcTextSize(label);
        var size = textSize + UiVec(28f, 15f);
        var origin = ImGui.GetCursorScreenPos();
        ImGui.Dummy(size);
        var fill = primary
            ? new Vector4(Accent.X, Accent.Y, Accent.Z, 0.92f)
            : AccentActive;
        var drawList = ImGui.GetWindowDrawList();
        drawList.AddRectFilled(origin, origin + size, ImGui.GetColorU32(fill), size.Y * 0.5f);
        drawList.AddText(origin + (size - textSize) * 0.5f, ImGui.GetColorU32(Vector4.One), label);
    }

    private void DrawActivityFeature(string label)
    {
        var origin = ImGui.GetCursorScreenPos();
        var height = Ui(29f);
        ImGui.Dummy(new Vector2(ImGui.GetContentRegionAvail().X, height));
        var drawList = ImGui.GetWindowDrawList();
        var center = origin + UiVec(12f, 14f);
        drawList.AddCircleFilled(center, Ui(11f), ImGui.GetColorU32(Accent));
        var check = FontAwesomeIcon.Check.ToIconString();
        var checkSize = ImGui.CalcTextSize(check);
        UiFonts.DrawIcon(drawList, ImGui.GetFontSize(), center - checkSize * 0.5f, ImGui.GetColorU32(Vector4.One), check);
        drawList.AddText(origin + UiVec(32f, 6f), ImGui.GetColorU32(new Vector4(0.89f, 0.90f, 0.97f, 1f)), label);
    }

    private bool DrawActivityLaunchButton(string id, string label, bool enabled, float width)
    {
        var size = new Vector2(width, Ui(40f));
        var origin = ImGui.GetCursorScreenPos();
        using var disabled = UiDisabled.Scope(!enabled);
        var clicked = ImGui.InvisibleButton($"##activityLaunch{id}", size);
        var hovered = enabled && ImGui.IsItemHovered();
        var drawList = ImGui.GetWindowDrawList();
        var fill = enabled
            ? hovered ? AccentHover : Accent
            : new Vector4(0.12f, 0.11f, 0.25f, 0.62f);
        drawList.AddRectFilled(origin, origin + size, ImGui.GetColorU32(fill), Ui(9f));
        if (!enabled)
        {
            drawList.AddRect(origin, origin + size, ImGui.GetColorU32(new Vector4(BorderSubtle.X, BorderSubtle.Y, BorderSubtle.Z, 0.75f)), Ui(9f));
        }
        var textSize = ImGui.CalcTextSize(label);
        drawList.AddText(origin + (size - textSize) * 0.5f, ImGui.GetColorU32(new Vector4(1f, 1f, 1f, enabled ? 1f : 0.65f)), label);
        return clicked;
    }

    private static void DrawCenteredActivityIcon(ImDrawListPtr drawList, FontAwesomeIcon icon, Vector2 origin, Vector2 size, float scale)
    {
        var text = icon.ToIconString();
        Vector2 textSize;
        using (UiFonts.PushIcon())
        {
            textSize = ImGui.CalcTextSize(text) * scale;
        }
        UiFonts.DrawIcon(drawList, ImGui.GetFontSize() * scale, origin + (size - textSize) * 0.5f, ImGui.GetColorU32(AccentHover), text);
    }

    private static void DrawActivitiesHeart(ImDrawListPtr drawList, Vector2 top, Vector2 extent, uint colour, float thickness)
    {
        drawList.AddBezierCubic(top, top + new Vector2(-extent.X, -extent.Y * 0.55f), top + new Vector2(-extent.X * 1.25f, extent.Y * 0.32f), top + new Vector2(0f, extent.Y), colour, thickness);
        drawList.AddBezierCubic(top, top + new Vector2(extent.X, -extent.Y * 0.55f), top + new Vector2(extent.X * 1.25f, extent.Y * 0.32f), top + new Vector2(0f, extent.Y), colour, thickness);
    }

    private static void DrawActivitiesFace(ImDrawListPtr drawList, Vector2 center, float radius, uint colour, float thickness)
    {
        drawList.AddCircle(center, radius, colour, 28, thickness);
        drawList.AddCircleFilled(center + new Vector2(-radius * 0.34f, -radius * 0.24f), Math.Max(Ui(1.3f), radius * 0.08f), colour);
        drawList.AddCircleFilled(center + new Vector2(radius * 0.34f, -radius * 0.24f), Math.Max(Ui(1.3f), radius * 0.08f), colour);
        drawList.AddBezierCubic(center + new Vector2(-radius * 0.45f, radius * 0.20f), center + new Vector2(-radius * 0.18f, radius * 0.55f), center + new Vector2(radius * 0.18f, radius * 0.55f), center + new Vector2(radius * 0.45f, radius * 0.20f), colour, Math.Max(Ui(1.8f), thickness * 0.65f));
    }
}

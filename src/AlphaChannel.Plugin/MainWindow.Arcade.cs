using AlphaChannel.Contracts;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private enum ArcadeLibraryFilter { All, Solo, Team, Favourites }

    private string arcadeSearch = string.Empty;
    private string arcadeCategory = "All categories";
    private ArcadeLibraryFilter arcadeFilter = ArcadeLibraryFilter.All;
    private string? arcadeRoomSignature;
    private string[] arcadeRoomSampleIds = [];
    private RoomDirectoryDto[] arcadeRoomSample = [];

    private void DrawArcadePage()
    {
        activityLibraryReturnPage = HomePage.Arcade;
        RefreshHomeWatchPartiesIfNeeded();
        using var spacing = ImRaii.PushStyle(ImGuiStyleVar.ItemSpacing, UiVec(12f, 12f));
        DrawArcadeBanner();
        DrawArcadeWatchParties();
        DrawArcadeNowPlaying();
        DrawArcadeToolbar();
        if (arcadeFilter == ArcadeLibraryFilter.All && arcadeCategory == "All categories" && string.IsNullOrWhiteSpace(arcadeSearch))
            DrawArcadeFeaturedGames();
        DrawArcadeLibrary();
    }

    private void DrawArcadeBanner()
    {
        var origin = ImGui.GetCursorScreenPos();
        var width = ImGui.GetContentRegionAvail().X;
        var compact = width < Ui(620f);
        var height = Ui(compact ? 170f : 134f);
        ImGui.Dummy(new Vector2(width, height));
        var draw = ImGui.GetWindowDrawList();
        DrawArcadeGradientPanel(draw, origin, new Vector2(width, height), HubSurface(0x0C1B33), HubSurface(0x040A15), HubTint(0x3489F5), 10f);
        DrawArcadePixelLabel(draw, origin + UiVec(22f, 20f));
        var textWidth = width - Ui(compact ? 44f : 292f);
        using (UiFonts.PushRelative(compact ? 2f : width < Ui(680f) ? 2.05f : 2.35f, emphasis: true))
            draw.AddText(origin + UiVec(22f, 42f), ImGui.GetColorU32(Vector4.One), ArcadeFitText("Pick your next game.", textWidth));
        using (UiFonts.PushRegularRelative(1.28f))
            DrawWrappedLines(draw, origin + UiVec(22f, 88f), textWidth, ImGui.GetTextLineHeight(), 2,
                ImGui.GetColorU32(ArcadeSecondaryText), "Play solo, compete or bring your party.", out _);
        DrawArcadePointsBadge(origin + new Vector2(compact ? Ui(22f) : width - Ui(142f), Ui(compact ? 116f : 14f)), Ui(128f));
        if (!compact)
        {
            draw.PushClipRect(origin + UiVec(1f, 1f), origin + new Vector2(width - Ui(1f), height), true);
            DrawArcadePixelCabinet(draw, origin + new Vector2(width - Ui(264f), height - Ui(118f)), Ui(1f));
            draw.PopClipRect();
        }
    }

    private void DrawArcadeFeaturedGames()
    {
        var headingOrigin = ImGui.GetCursorScreenPos();
        ArcadeSectionHeading("Featured games");
        if (ImGui.GetContentRegionAvail().X >= Ui(520f))
        {
            float headingWidth;
            using (UiFonts.PushRelative(1.4f, emphasis: true)) headingWidth = ImGui.CalcTextSize("Featured games").X;
            using (UiFonts.PushRegularRelative(.94f))
                ImGui.GetWindowDrawList().AddText(headingOrigin + new Vector2(headingWidth + Ui(12f), Ui(5f)),
                    ImGui.GetColorU32(ArcadeSecondaryText), "Hand-picked for your next session.");
        }
        var featured = new[] { "racePreview", "poker" }.Select(id => ActivityLibrary.First(entry => entry.Id == id)).ToArray();
        var width = ImGui.GetContentRegionAvail().X;
        var gap = Ui(12f);
        var columns = width >= Ui(660f) ? 2 : 1;
        var cardWidth = (width - gap * (columns - 1)) / columns;
        var height = MathF.Min(Ui(150f), cardWidth * .38f) + Ui(20f);
        var start = ImGui.GetCursorScreenPos();
        for (var i = 0; i < featured.Length; i++)
        {
            ImGui.SetCursorScreenPos(start + new Vector2(i % columns * (cardWidth + gap), i / columns * (height + gap)));
            DrawArcadeFeaturedCard(featured[i], cardWidth, height);
        }
        var rows = (featured.Length + columns - 1) / columns;
        ImGui.SetCursorScreenPos(start + new Vector2(0, rows * height + (rows - 1) * gap));
        ImGui.Dummy(Vector2.Zero);
    }

    private void DrawArcadeFeaturedCard(ActivityLibraryEntry activity, float width, float height)
    {
        DrawArcadeGameCard(activity, width, height, featured: true);
    }

    private void DrawArcadeWatchParties()
    {
        var width = ImGui.GetContentRegionAvail().X;
        var arcadeRooms = homeWatchPartyRooms
            .Where(room => room.HasMedia &&
                GetPartyDirectoryContentType(room) == PartyDirectoryContentType.Activities)
            .ToArray();
        var signature = string.Join("|", arcadeRooms
            .Select(room => room.HostAccountId)
            .OrderBy(id => id, StringComparer.Ordinal));
        if (!string.Equals(arcadeRoomSignature, signature, StringComparison.Ordinal))
        {
            arcadeRoomSignature = signature;
            arcadeRoomSampleIds = arcadeRooms
                .OrderBy(_ => Random.Shared.Next())
                .Take(3).Select(room => room.HostAccountId).ToArray();
        }
        arcadeRoomSample = arcadeRoomSampleIds
            .Select(id => arcadeRooms.FirstOrDefault(room => room.HostAccountId == id))
            .OfType<RoomDirectoryDto>().ToArray();

        if (arcadeRoomSample.Length == 0)
        {
            var origin = ImGui.GetCursorScreenPos();
            var compact = width < Ui(540f);
            var size = new Vector2(width, Ui(compact ? 104f : 64f));
            ImGui.Dummy(size);
            var draw = ImGui.GetWindowDrawList();
            DrawArcadeGradientPanel(draw, origin, size, HubSurface(0x0B172B), HubSurface(0x070E1D), ArcadeCardBorder, 9f);
            UiFonts.DrawIcon(draw, Ui(32f), origin + UiVec(16f, 17f), ImGui.GetColorU32(AccentHover), FontAwesomeIcon.Users.ToIconString());
            using (UiFonts.PushRelative(1.1f, emphasis: true))
                draw.AddText(origin + UiVec(72f, 12f), ImGui.GetColorU32(Vector4.One), "Playing together");
            using (UiFonts.PushRegularRelative(1f))
                DrawWrappedLines(draw, origin + UiVec(72f, 35f), width - Ui(compact ? 88f : 230f), ImGui.GetTextLineHeight(), 2,
                    ImGui.GetColorU32(ArcadeSecondaryText), "Bring your friends into the arcade.", out _);
            ImGui.SetCursorScreenPos(origin + new Vector2(compact ? Ui(72f) : width - Ui(150f), Ui(compact ? 64f : 15f)));
            if (DrawArcadePartyButton(Ui(136f))) currentPage = HomePage.WatchAlong;
            ImGui.SetCursorScreenPos(origin + new Vector2(0f, size.Y));
            ImGui.Dummy(Vector2.Zero);
            return;
        }

        ArcadeSectionHeading("Playing together", "View all", () =>
        {
            partyDirectoryContentTypeFilter = PartyDirectoryContentType.Activities;
            currentPage = HomePage.PartyDirectory;
        });
        var gap = Ui(8f);
        var height = Ui(76f);
        var start = ImGui.GetCursorScreenPos();
        for (var index = 0; index < arcadeRoomSample.Length; index++)
        {
            ImGui.SetCursorScreenPos(start + new Vector2(0f, index * (height + gap)));
            DrawArcadeRoomCard(arcadeRoomSample[index], width, height);
        }
        ImGui.SetCursorScreenPos(start + new Vector2(0f,
            arcadeRoomSample.Length * height + (arcadeRoomSample.Length - 1) * gap));
        ImGui.Dummy(Vector2.Zero);
    }

    private void DrawArcadeRoomCard(RoomDirectoryDto room, float width, float height)
    {
        var origin = ImGui.GetCursorScreenPos();
        var size = new Vector2(width, height);
        using var id = ImRaii.PushId("arcadeRoom" + room.HostAccountId);
        ImGui.Dummy(size);
        var draw = ImGui.GetWindowDrawList();
        draw.AddRectFilled(origin, origin + size, ImGui.GetColorU32(HubSurface(0x121A30)), Ui(9f));
        draw.AddRect(origin, origin + size, ImGui.GetColorU32(BorderSubtle), Ui(9f));
        TryGetPartyDirectoryActivityTitle(room, out var activityTitle);
        var activity = ActivityLibrary.FirstOrDefault(entry =>
            string.Equals(entry.Title, activityTitle, StringComparison.OrdinalIgnoreCase));
        ImGui.SetCursorScreenPos(origin + UiVec(10f, 10f));
        DrawActivityPreview("arcadeRoom" + room.HostAccountId,
            activity?.Preview ?? ActivityPreviewKind.ComingSoon, Ui(56f), 56f, 5f, 4f, false);
        var text = origin + UiVec(80f, 15f);
        var textWidth = width - Ui(188f);
        DrawArcadeCardTitle(draw, text, room.HostDisplayName + "'s party", textWidth);
        using (UiFonts.PushRelative(.86f))
            draw.AddText(text + UiVec(0f, 27f), ImGui.GetColorU32(MutedText), ArcadeFitText(
                (string.IsNullOrWhiteSpace(activityTitle) ? "Playing an activity" : activityTitle) +
                $" \u00b7 {room.ViewerCount + 1} members", textWidth));
        ImGui.SetCursorScreenPos(origin + new Vector2(width - Ui(94f), Ui(20f)));
        if (AlphaChatButton("arcadeRoomJoin", "Join", FontAwesomeIcon.Users, Ui(80f), Ui(36f), primary: true))
            JoinHomeWatchParty(room);
        ImGui.SetCursorScreenPos(origin + new Vector2(0f, height));
    }

    private void DrawArcadeLibrary()
    {
        var width = ImGui.GetContentRegionAvail().X;
        var heading = ImGui.GetCursorScreenPos();
        ArcadeSectionHeading("All games");
        var after = ImGui.GetCursorScreenPos();
        var playing = arcadePresence?.Count(DateTime.UtcNow);
        var label = playing is { } count ? $"{count:N0} playing now" : "Connecting…";
        var countWidth = Ui(24f) + ImGui.CalcTextSize(label).X;
        DrawArcadePlayingCount(heading + new Vector2(width - countWidth, Ui(2f)), label, countWidth);
        ImGui.SetCursorScreenPos(after);
        var visible = ActivityLibrary.Where(ArcadeMatchesFilters).ToArray();
        if (visible.Length == 0)
        {
            ImGui.TextColored(MutedText, arcadeFilter == ArcadeLibraryFilter.Favourites ? "No favourite games yet. Select a star on a game to add it here." : "No games match those filters.");
            return;
        }
        var gap = Ui(12f);
        var columns = width >= Ui(1060f) ? 3 : width >= Ui(660f) ? 2 : 1;
        var cardWidth = (width - gap * (columns - 1)) / columns;
        var cardHeight = MathF.Min(Ui(104f), cardWidth * .28f) + Ui(20f);
        var start = ImGui.GetCursorScreenPos();
        for (var index = 0; index < visible.Length; index++)
        {
            ImGui.SetCursorScreenPos(start + new Vector2(index % columns * (cardWidth + gap), index / columns * (cardHeight + gap)));
            DrawArcadeLibraryCard(visible[index], cardWidth, cardHeight);
        }
        var rows = (visible.Length + columns - 1) / columns;
        ImGui.SetCursorScreenPos(start + new Vector2(0f, rows * cardHeight + (rows - 1) * gap));
        ImGui.Dummy(Vector2.Zero);
    }

    private void DrawArcadePlayingCount(Vector2 origin,string label,float width)
    {
        var saved=ImGui.GetCursorScreenPos();
        ImGui.SetCursorScreenPos(origin);
        ImGui.InvisibleButton("##arcadePlayingCount",new Vector2(width,Ui(25f)));
        var draw=ImGui.GetWindowDrawList();
        UiFonts.DrawIcon(draw,ImGui.GetFontSize(),origin+UiVec(0f,2f),ImGui.GetColorU32(AccentHover),FontAwesomeIcon.User.ToIconString());
        draw.AddText(origin+UiVec(24f,2f),ImGui.GetColorU32(MutedText),label);
        ImGui.SetCursorScreenPos(saved);
    }

    private void DrawArcadeToolbar()
    {
        var width = ImGui.GetContentRegionAvail().X;
        var gap = Ui(8f);
        var categoryWidth = Ui(width < Ui(480f) ? 130f : 150f);
        using var colours = ImRaii.PushColor(ImGuiCol.FrameBg, CardBg).Push(ImGuiCol.FrameBgHovered, CardBgHover).Push(ImGuiCol.Border, BorderSubtle);
        using var style = ImRaii.PushStyle(ImGuiStyleVar.FrameRounding, Ui(8f)).Push(ImGuiStyleVar.FrameBorderSize, Ui(1f));
        var searchOrigin = ImGui.GetCursorScreenPos();
        ImGui.SetNextItemWidth(MathF.Max(Ui(80f), width - categoryWidth - gap));
        using (ImRaii.PushStyle(ImGuiStyleVar.FramePadding, UiVec(34f, 12f)))
            ImGui.InputTextWithHint("##arcadeSearch", "Search games...", ref arcadeSearch, 100);
        UiFonts.DrawIcon(ImGui.GetWindowDrawList(), ImGui.GetFontSize(), searchOrigin + UiVec(12f, 12f),
            ImGui.GetColorU32(ArcadeSecondaryText), FontAwesomeIcon.Search.ToIconString());
        ImGui.SameLine(0f, gap);
        using (ImRaii.PushStyle(ImGuiStyleVar.FramePadding, UiVec(12f, 12f)))
            DrawArcadeCategory(categoryWidth);
        DrawArcadeFilters(gap);
    }

    private void DrawArcadeFilters(float gap)
    {
        var width = ImGui.GetContentRegionAvail().X;
        var origin = ImGui.GetCursorScreenPos();
        var tabWidth = MathF.Min(Ui(126f), (width - gap * 3f) / 4f);
        DrawArcadeFilter("All games", ArcadeLibraryFilter.All, tabWidth);
        ImGui.SameLine(0f, gap);
        DrawArcadeFilter("Favourites", ArcadeLibraryFilter.Favourites, tabWidth);
        ImGui.SameLine(0f, gap);
        DrawArcadeFilter("Solo", ArcadeLibraryFilter.Solo, tabWidth);
        ImGui.SameLine(0f, gap);
        DrawArcadeFilter("Multiplayer", ArcadeLibraryFilter.Team, tabWidth);
        ImGui.GetWindowDrawList().AddLine(origin + UiVec(0f, 39f), origin + new Vector2(width, Ui(39f)), ImGui.GetColorU32(BorderSubtle));
    }

    private void DrawArcadeFilter(string label, ArcadeLibraryFilter filter, float width)
    {
        var origin = ImGui.GetCursorScreenPos();
        var selected = arcadeFilter == filter;
        if (ImGui.InvisibleButton("##arcadeFilter" + label, new Vector2(width, Ui(40f)))) arcadeFilter = filter;
        var draw = ImGui.GetWindowDrawList();
        var colour = selected || ImGui.IsItemHovered() ? AccentHover : ArcadeSecondaryText;
        using (UiFonts.PushRelative(1.04f, emphasis: selected))
        {
            var text = ArcadeFitText(label, width - Ui(8f));
            draw.AddText(origin + new Vector2((width - ImGui.CalcTextSize(text).X) * .5f, Ui(10f)), ImGui.GetColorU32(colour), text);
        }
        if (selected) draw.AddRectFilled(origin + UiVec(0f, 37f), origin + new Vector2(width, Ui(40f)), ImGui.GetColorU32(Accent), Ui(2f));
    }

    private void DrawArcadeCategory(float width)
    {
        var origin = ImGui.GetCursorScreenPos();
        var draw = ImGui.GetWindowDrawList();
        ImGui.SetNextItemWidth(width);
        var opened = UiFonts.Window(ImGui.GetFontSize(), ImGui.BeginCombo("##arcadeCategory", arcadeCategory, ImGuiComboFlags.NoArrowButton));
        var centre = origin + new Vector2(width - Ui(18f), Ui(19f));
        var ink = ImGui.GetColorU32(ArcadeSecondaryText);
        draw.AddLine(centre + UiVec(-4f, -2f), centre + UiVec(0f, 2f), ink, Ui(1.5f));
        draw.AddLine(centre + UiVec(0f, 2f), centre + UiVec(4f, -2f), ink, Ui(1.5f));
        if (!opened) return;
        foreach (var category in ActivityCategories)
        {
            if (ImGui.Selectable(category, arcadeCategory == category))
                arcadeCategory = category;
        }
        ImGui.EndCombo();
    }

    private bool ArcadeMatchesFilters(ActivityLibraryEntry activity)
    {
        // Keep the game available internally while hiding its arcade listing.
        if (activity.Id == "blastParty") return false;
        if (!string.IsNullOrWhiteSpace(arcadeSearch) &&
            !activity.Title.Contains(arcadeSearch, StringComparison.OrdinalIgnoreCase) &&
            !activity.Description.Contains(arcadeSearch, StringComparison.OrdinalIgnoreCase) &&
            !activity.Categories.Any(category => category.Contains(arcadeSearch,
                StringComparison.OrdinalIgnoreCase))) return false;
        if (arcadeCategory != "All categories" && !activity.Categories.Contains(arcadeCategory))
            return false;
        return arcadeFilter switch
        {
            ArcadeLibraryFilter.Solo => activity.SupportsSolo,
            ArcadeLibraryFilter.Team => activity.SupportsTeam,
            ArcadeLibraryFilter.Favourites => IsArcadeFavourite(activity.Id),
            _ => true,
        };
    }

    private void DrawArcadeLibraryCard(ActivityLibraryEntry activity, float width, float height)
    {
        DrawArcadeGameCard(activity, width, height, featured: false);
    }

    private bool IsArcadeFavourite(string activityId) =>
        Plugin.Cfg.FavouriteArcadeActivityIds.Contains(activityId, StringComparer.Ordinal);

    private static (string Game, bool Cumulative)? ArcadeCardScoreboard(string activityId) => activityId switch
    {
        "pictionary" => ("pictionary", true),
        "pictionaryTeams" => ("pictionary-teams", true),
        "quiz" => ("quiz", true),
        "skyRail" => ("air-force", true),
        "racePreview" => ("choco-race", false),
        "slotMachine" => ("slot-machine", false),
        "poker" => ("poker", false),
        "roulettePrototype" => ("roulette", false),
        _ => null,
    };

    private bool DrawArcadeCardTrophy(ActivityLibraryEntry activity, Vector2 cardOrigin,
        float cardWidth, float cardHeight, float bottomOffset)
    {
        if (ArcadeCardScoreboard(activity.Id) is not { } scoreboard) return false;
        var origin = cardOrigin + new Vector2(cardWidth - Ui(36f), cardHeight - bottomOffset);
        var size = UiVec(28f, 28f);
        ImGui.SetCursorScreenPos(origin);
        if (ImGui.InvisibleButton("##highscores", size))
            OpenArcadeScoreboard(scoreboard.Game, activity.Title, scoreboard.Cumulative);
        var hovered = ImGui.IsItemHovered();
        var draw = ImGui.GetWindowDrawList();
        if (hovered)
            draw.AddRectFilled(origin, origin + size, ImGui.GetColorU32(FrameBgHover), Ui(6f));
        var glyph = FontAwesomeIcon.Trophy.ToIconString();
        Vector2 glyphSize;
        var glyphPixels = ImGui.GetFontSize() * 1.28f;
        using (UiFonts.PushIcon(glyphPixels)) glyphSize = ImGui.CalcTextSize(glyph);
        UiFonts.DrawIcon(draw, glyphPixels, origin + (size - glyphSize) * 0.5f, ImGui.GetColorU32(hovered ? Gold : AccentHover), glyph);
        // The trophy becomes the active ImGui item on mouse-down, which can make
        // IsWindowHovered() false. The card already checks window hover before
        // drawing its controls; exclude the trophy rectangle regardless here.
        return ImGui.IsMouseHoveringRect(origin, origin + size);
    }

    private Vector2 DrawArcadeFavouriteStar(ActivityLibraryEntry activity, Vector2 cardOrigin, float cardWidth)
    {
        var starOrigin = new Vector2(cardOrigin.X + cardWidth - Ui(36f), cardOrigin.Y + Ui(7f));
        var starSize = UiVec(28f, 28f);
        ImGui.SetCursorScreenPos(starOrigin);
        if (ImGui.InvisibleButton("##favourite", starSize))
        {
            if (IsArcadeFavourite(activity.Id))
                Plugin.Cfg.FavouriteArcadeActivityIds.RemoveAll(id =>
                    string.Equals(id, activity.Id, StringComparison.Ordinal));
            else
                Plugin.Cfg.FavouriteArcadeActivityIds.Add(activity.Id);
            Plugin.Cfg.Save();
        }

        var favourite = IsArcadeFavourite(activity.Id);
        var draw = ImGui.GetWindowDrawList();
        if (favourite)
            UiFonts.DrawIcon(draw, ImGui.GetFontSize() * 1.12f, starOrigin + UiVec(5f, 4f),
                ImGui.GetColorU32(Gold), FontAwesomeIcon.Star.ToIconString());
        else
        {
            var centre = starOrigin + UiVec(14f, 14f);
            var ink = ImGui.GetColorU32(ImGui.IsItemHovered() ? AccentHover : MutedText);
            for (var i = 0; i < 10; i++)
            {
                Vector2 Point(int index)
                {
                    var angle = -MathF.PI / 2f + index * MathF.PI / 5f;
                    return centre + new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * Ui(index % 2 == 0 ? 10.5f : 4.6f);
                }
                draw.AddLine(Point(i), Point((i + 1) % 10), ink, Ui(1.9f));
            }
        }
        if (ImGui.IsItemHovered())
            UiFonts.SetTooltip(favourite ? "Remove from favourite games" : "Add to favourite games");
        return starOrigin;
    }

    private void DrawArcadePointsBadge(Vector2 origin, float width)
    {
        var size = new Vector2(width, Ui(48f));
        var draw = ImGui.GetWindowDrawList();
        DrawArcadeGradientPanel(draw, origin, size, HubSurface(0x0B192F), HubSurface(0x030914), ArcadeCardBorder, 7f);
        DrawArcadeStar(draw, origin + UiVec(23f, 24f), Ui(15f), ImGui.GetColorU32(Gold));
        var accountId = CurrentSession?.AccountId;
        var points = string.IsNullOrWhiteSpace(accountId) || !activityPoints.StorageHealthy
            ? "--"
            : activityPoints.GetPoints(accountId).ToString("N0", System.Globalization.CultureInfo.InvariantCulture);
        using (UiFonts.PushRelative(1.1f, emphasis: true))
            draw.AddText(origin + UiVec(46f, 8f), ImGui.GetColorU32(Gold), ArcadeFitText(points, width - Ui(54f)));
        using (UiFonts.PushRegularRelative(.82f))
            draw.AddText(origin + UiVec(46f, 29f), ImGui.GetColorU32(ArcadeSecondaryText), "Arcade Points");
    }

    private void DrawArcadeEndButton(Vector2 origin, float width)
    {
        var activity = ActiveActivity;
        var canEnd = activity != ActiveActivityKind.None && IsLocalActivityHost;
        ImGui.SetCursorScreenPos(origin);
        using (UiDisabled.Scope(!canEnd))
        using (ImRaii.PushColor(ImGuiCol.Button, new Vector4(0.32f, 0.08f, 0.13f, 1f))
            .Push(ImGuiCol.ButtonHovered, new Vector4(0.51f, 0.12f, 0.18f, 1f)))
        {
            if (ImGui.Button("Stop##arcadeEnd", new Vector2(width, Ui(32f))))
                EndCurrentActivity(activity);
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled) && !canEnd)
            UiFonts.SetTooltip(activity == ActiveActivityKind.None
                ? "No activity is currently active"
                : "Only the Watch Party host can end this activity");
    }

    private void ArcadeSectionHeading(string heading, string? action = null, Action? onAction = null)
    {
        var start = ImGui.GetCursorScreenPos();
        var width = ImGui.GetContentRegionAvail().X;
        using var uiFontScale32130=UiFonts.PushRelative(1.4f, emphasis: true);
        ImGui.TextColored(Hex(0xE2E7EF), heading);
        uiFontScale32130.Dispose();
        if (action is not null && onAction is not null)
        {
            var after = ImGui.GetCursorScreenPos();
            var label = action + "  >";
            var actionWidth = ImGui.CalcTextSize(label).X + Ui(8f);
            var actionOrigin = new Vector2(start.X + width - actionWidth, start.Y + Ui(2f));
            ImGui.SetCursorScreenPos(actionOrigin);
            if (ImGui.InvisibleButton($"##arcade{heading}", new Vector2(actionWidth, Ui(25f))))
                onAction();
            ImGui.GetWindowDrawList().AddText(actionOrigin + UiVec(4f, 4f),
                ImGui.GetColorU32(ImGui.IsItemHovered() ? Vector4.One : MutedText), label);
            ImGui.SetCursorScreenPos(after);
        }
    }

    private void OpenArcadeActivity(ActivityLibraryEntry activity)
    {
        activityLibraryReturnPage = HomePage.Arcade;
        OpenActivityLibraryEntry(activity);
    }

    private static ActiveActivityKind ArcadeActivityKind(string id) => id switch
    {
        "whiteboard" => ActiveActivityKind.Whiteboard,
        "pictionary" => ActiveActivityKind.Pictionary,
        "pictionaryTeams" => ActiveActivityKind.PictionaryTeams,
        "quiz" => ActiveActivityKind.Quiz,
        "roulettePrototype" => ActiveActivityKind.Roulette,
        "bingo" => ActiveActivityKind.Bingo,
        "racePreview" => ActiveActivityKind.ChocoRaceBets,
        "blastParty" => ActiveActivityKind.BlastParty,
        "skyRail" => ActiveActivityKind.SkyRail,
        "pixels" => ActiveActivityKind.Pixels,
        "luckyWheel" => ActiveActivityKind.LuckyWheel,
        "slotMachine" => ActiveActivityKind.SlotMachine,
        "poker" => ActiveActivityKind.Poker,
        _ => ActiveActivityKind.None,
    };

    private static string ArcadeFitText(string text, float maxWidth)
    {
        if (ImGui.CalcTextSize(text).X <= maxWidth) return text;
        while (text.Length > 0 && ImGui.CalcTextSize(text + "…").X > maxWidth)
            text = text[..^1];
        return text + "…";
    }

    private static void ArcadeDrawDescription(ImDrawListPtr draw, string text,
        Vector2 origin, float maxWidth, int maxLines)
    {
        using var descriptionFont = UiFonts.PushRelative(.94f);
        DrawWrappedLines(draw, origin, maxWidth, ImGui.GetTextLineHeight(), maxLines,
            ImGui.GetColorU32(ArcadeSecondaryText), text, out var clipped);
        if (clipped && ImGui.IsMouseHoveringRect(origin,
                origin + new Vector2(maxWidth, maxLines * ImGui.GetTextLineHeight())))
            UiFonts.SetTooltip(text);
    }
}

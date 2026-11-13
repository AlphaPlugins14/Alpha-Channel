using AlphaChannel.Plugin.Auth;
using AlphaChannel.Contracts;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private readonly FriendsDirectoryClient friendsDirectoryApi = new();
    private static readonly string[] DirectoryLooking = ["Watching together", "Gaming together", "Chatting", "Friends", "Hangouts", "Shared Interests", "Any"];
    private static readonly string[] DirectoryLookingFilters = ["All", .. DirectoryLooking.Where(option => option != "Any")];
    private static readonly (string Category, string[] Tags)[] DirectorySuggestedTags =
    [
        ("Watch", ["Films", "Anime", "Documentaries", "YouTube", "Cartoons", "Horror films", "Comedy", "Sci-Fi", "Fantasy", "Drama", "Action", "Romance"]),
        ("Listen", ["Music", "Pop", "Rock", "Electronic", "Soundtracks", "Radio", "Musicals", "Live DJs", "Jazz", "Classical", "Metal", "Hip-Hop", "Indie", "R&B"]),
        ("Playing", ["Retro Games", "Arcade", "Party Games"]),
        ("Activities", ["Watch Together", "Movie Nights", "DJing", "Gaming"]),
        ("Social", ["Casual Chat", "New Friends", "Small Groups", "Hangouts", "Venues", "Watch and Chat"])
    ];
    private string? directorySessionToken;
    private FriendDirectoryPerson[] directoryPeople = [];
    private FriendDirectoryPerson? directoryOwn;
    private bool directoryOwnLoaded, directoryOwnBusy, directoryOwnValid, directoryBusy, directoryDirty = true;
    private int directoryGeneration, directoryOwnGeneration;
    private string directorySearch = "";
    private string directoryInterestSearch = "";
    private string? directoryError, directoryOwnError, directoryNextCursor;
    private readonly List<string> directoryTags = [];
    private int directoryLookingFilter, directorySort;
    private bool directoryOnlineOnly;
    private DateTime directorySearchAfter;
    private readonly HashSet<string> directoryPartyJoins = new(StringComparer.OrdinalIgnoreCase);

    private void OpenFriendsDirectory()
    {
        if (!AllowInlineProfileNavigation()) return;
        currentPage = HomePage.FriendsDirectory;
        navigationScroll.RequestReset();
        MarkDirectoryDirty();
    }
    private void EnsureDirectorySession(CharacterSession session)
    {
        if (directorySessionToken == session.Token) return;
        directorySessionToken = session.Token;
        directoryGeneration++; directoryOwnGeneration++;
        directoryPeople = []; directoryOwn = null; directoryNextCursor = null;
        directoryOwnLoaded = directoryOwnValid = directoryOwnBusy = directoryBusy = false;
        directoryEditorOpen = false; directoryReportTarget = null;
        directoryMutationBusy = false; directoryRemoveConfirm = false;
        directoryError = directoryOwnError = null;
        directorySearch = ""; directoryTags.Clear();
        directoryLookingFilter = directorySort = 0;
        directoryOnlineOnly = false; MarkDirectoryDirty();
    }
    private void MarkDirectoryDirty()
    {
        directoryGeneration++;
        directoryDirty = true;
        directorySearchAfter = DateTime.UtcNow.AddMilliseconds(350);
    }
    private bool DirectorySessionCurrent(CharacterSession session) => Volatile.Read(ref disposeStarted) == 0 &&
        CurrentSession?.Token == session.Token && directorySessionToken == session.Token;
    private async Task LoadDirectoryOwn(CharacterSession session)
    {
        var generation = ++directoryOwnGeneration;
        directoryOwnBusy = true; directoryOwnError = null;
        try
        {
            var result = await friendsDirectoryApi.Own(session.Token, youtubeFeedToken).ConfigureAwait(false);
            await Plugin.Framework.RunOnFrameworkThread(() =>
            {
                if (!DirectorySessionCurrent(session) || generation != directoryOwnGeneration) return;
                directoryOwn = result.Listing; directoryOwnValid = true;
            });
        }
        catch (Exception ex)
        {
            await Plugin.Framework.RunOnFrameworkThread(() =>
            {
                if (DirectorySessionCurrent(session) && generation == directoryOwnGeneration)
                { directoryOwnError = DirectoryFailure(ex); directoryOwnValid = false; }
            });
        }
        finally
        {
            await Plugin.Framework.RunOnFrameworkThread(() =>
            {
                if (DirectorySessionCurrent(session) && generation == directoryOwnGeneration)
                { directoryOwnBusy = false; directoryOwnLoaded = true; }
            });
        }
    }
    private static string DirectoryFailure(Exception ex) => ex is InvalidOperationException
        ? ex.Message : "The friends directory is temporarily unavailable. Please try again.";
    private async Task LoadDirectoryPeople(CharacterSession session, bool more)
    {
        var generation = directoryGeneration;
        var query = directorySearch.Trim(); var tags = directoryTags.ToArray();
        var looking = directoryLookingFilter == 0 ? "" : DirectoryLookingFilters[directoryLookingFilter];
        var online = directoryOnlineOnly; var sort = new[] { "discover", "shared", "new" }[directorySort];
        var cursor = more ? directoryNextCursor : null;
        directoryBusy = true; directoryDirty = false; directoryError = null;
        if (!more) { directoryPeople = []; directoryNextCursor = null; }
        try
        {
            var page = await friendsDirectoryApi.Browse(session.Token, query, tags, looking,
                online, sort, cursor, youtubeFeedToken).ConfigureAwait(false);
            await Plugin.Framework.RunOnFrameworkThread(() =>
            {
                if (!DirectorySessionCurrent(session) || generation != directoryGeneration) return;
                directoryPeople = (more ? directoryPeople.Concat(page.People) : page.People)
                    .DistinctBy(p => p.AccountId).ToArray();
                ObservePatreonPeople(page.People.Select(p => p.AccountId));
                directoryNextCursor = page.NextCursor;
            });
        }
        catch (Exception ex)
        {
            await Plugin.Framework.RunOnFrameworkThread(() =>
            {
                if (DirectorySessionCurrent(session) && generation == directoryGeneration) directoryError = DirectoryFailure(ex);
            });
        }
        finally
        {
            await Plugin.Framework.RunOnFrameworkThread(() =>
            {
                // A changed search leaves Dirty set, then starts when this request finishes.
                if (DirectorySessionCurrent(session)) directoryBusy = false;
            });
        }
    }
    private void DrawFriendsDirectoryPage()
    {
        if (CurrentSession is not { } session)
        { DrawPlainEmpty("Sign in to find friends.", "Open Settings", () => currentPage = HomePage.Settings); return; }
        using var directoryFonts = BeginUiFonts();
        EnsureDirectorySession(session);
        if (!directoryOwnLoaded && !directoryOwnBusy) _ = LoadDirectoryOwn(session);
        if (directoryDirty && !directoryBusy && DateTime.UtcNow >= directorySearchAfter) _ = LoadDirectoryPeople(session, false);
        if (friendsDirty && !friendsLoading) RefreshFriends(session.Token);
        using var style = ImRaii.PushStyle(ImGuiStyleVar.FrameRounding, Ui(8)).Push(ImGuiStyleVar.ChildRounding, Ui(10))
            .Push(ImGuiStyleVar.WindowPadding, UiVec(14, 12)).Push(ImGuiStyleVar.ItemSpacing, UiVec(10, 10))
            .Push(ImGuiStyleVar.FramePadding, UiVec(10, 8)).Push(ImGuiStyleVar.ChildBorderSize, Ui(1))
            .Push(ImGuiStyleVar.FrameBorderSize, Ui(1));
        using var colors = ImRaii.PushColor(ImGuiCol.ChildBg, FrameBg).Push(ImGuiCol.FrameBg, FrameBg)
            .Push(ImGuiCol.Border, AccentInk(.35f)).Push(ImGuiCol.Button, FrameBg).Push(ImGuiCol.ButtonHovered, FrameBgHover);
        var width = ImGui.GetContentRegionAvail().X; var origin = ImGui.GetCursorScreenPos();
        ImGui.SetCursorScreenPos(origin + UiVec(0, 5));
        DrawSocialHeading("Discover the community", 1.45f);
        ImGui.SetCursorScreenPos(origin + new Vector2(width < Ui(560) ? 0 : width - Ui(178), width < Ui(560) ? Ui(32) : 0));
        using (UiDisabled.Scope(!directoryOwnValid || directoryOwnBusy))
            if (AlphaChatButton("directoryEdit", directoryOwn is null ? "List my profile" : "My listing", FontAwesomeIcon.UserPlus, Ui(178), Ui(38), primary: true))
                _ = RefreshAndOpenDirectoryEditor(session);
        ImGui.SetCursorScreenPos(origin + new Vector2(0, Ui(width < Ui(560) ? 80 : 52)));
        ImGui.SetNextItemWidth(-1);
        using (ImRaii.PushStyle(ImGuiStyleVar.FramePadding, new Vector2(Ui(42), MathF.Max(Ui(6), (Ui(42) - ImGui.GetFontSize()) * .5f))))
            if (ImGui.InputTextWithHint("##directorySearch", "Search people, introductions or interests…", ref directorySearch, 80)) MarkDirectoryDirty();
        var searchMin = ImGui.GetItemRectMin(); var searchMax = ImGui.GetItemRectMax();
        UiFonts.DrawIcon(ImGui.GetWindowDrawList(), ImGui.GetFontSize(), searchMin + new Vector2(Ui(14), (searchMax.Y - searchMin.Y - ImGui.GetFontSize()) * .5f),
            ImGui.GetColorU32(MutedText), FontAwesomeIcon.Search.ToIconString());
        ImGui.Dummy(UiVec(0, 2));
        DrawDirectoryFilters();
        if (directoryOwnError is { } ownError)
        {
            ImGui.TextWrapped(ownError);
            if (AlphaChatButton("directoryReloadOwn", "Reload my listing", FontAwesomeIcon.Redo, Ui(165))) _ = LoadDirectoryOwn(session);
        }
        if (directoryError is { } error)
        {
            ImGui.TextWrapped(error);
            if (AlphaChatButton("directoryRetry", "Retry", FontAwesomeIcon.Redo, Ui(85))) MarkDirectoryDirty();
        }
        var others = directoryPeople.Where(p => p.AccountId != session.AccountId && !AlphaChatBlocked(p.AccountId));
        var people = directoryOwnValid && directoryOwn is { Visible: true, Suspended: false } own
            ? others.Prepend(own with
            {
                PublicPartyHostId = own.ShowParty && stream.IsConnected && stream.Mode == StreamMode.Hosting &&
                    !stream.IsPrivate && stream.RoomKind is RoomKind.Public or RoomKind.Venue ? session.AccountId : null
            }).ToArray() : others.ToArray();
        DrawDirectoryFilterSummary(directoryBusy ? "Finding people…" : $"Results ({people.Length})"
            + (directoryNextCursor is null ? "" : " · More available"));
        var columns = width >= Ui(620) ? 2 : 1;
        var cardWidth = (width - Ui(12) * (columns - 1)) / columns;
        for (var i = 0; i < people.Length; i += columns)
        {
            var left = MeasureDirectoryPerson(people[i], cardWidth, people[i].AccountId == session.AccountId);
            var right = columns == 2 && i + 1 < people.Length
                ? MeasureDirectoryPerson(people[i + 1], cardWidth, people[i + 1].AccountId == session.AccountId) : null;
            var rowHeight = MathF.Max(left.Height, right?.Height ?? 0);
            using (ImRaii.PushId(people[i].AccountId)) DrawDirectoryPerson(session, people[i], cardWidth, rowHeight, left);
            if (right is not null)
            {
                ImGui.SameLine(0, Ui(12));
                using (ImRaii.PushId(people[i + 1].AccountId)) DrawDirectoryPerson(session, people[i + 1], cardWidth, rowHeight, right);
            }
        }
        if (!directoryBusy && people.Length == 0 && directoryError is null)
            ImGui.TextWrapped("No people match this page of results. Try other filters or load more people.");
        using (UiDisabled.Scope(directoryBusy || directoryDirty))
        {
            if (directoryNextCursor is not null)
            {
                ImGui.SetCursorPosX(ImGui.GetCursorPosX() + MathF.Max(0, (width - Ui(150)) * .5f));
                if (AlphaChatButton("directoryMore", "Load more", FontAwesomeIcon.ChevronDown, Ui(150))) _ = LoadDirectoryPeople(session, true);
            }
        }
    }
    private void DrawDirectoryFilters()
    {
        var origin = ImGui.GetCursorScreenPos(); var width = ImGui.GetContentRegionAvail().X;
        var height = Ui(38); var gap = Ui(10);
        var interestWidth = Ui(125); var lookingWidth = Ui(180); var sortWidth = Ui(190);
        var onlineWidth = ImGui.CalcTextSize("Online now").X + Ui(30);
        var sortGroupWidth = sortWidth + gap + height;
        var singleRow = width >= interestWidth + lookingWidth + onlineWidth + sortGroupWidth + gap * 3;
        using var popupStyle = ImRaii.PushStyle(ImGuiStyleVar.PopupBorderSize, Ui(1))
            .Push(ImGuiStyleVar.PopupRounding, Ui(10));
        using var popupColors = ImRaii.PushColor(ImGuiCol.Border, AccentInk(.7f)).Push(ImGuiCol.PopupBg, CardBg);
        if (DirectoryFilterButton("directoryInterestFilters", "Interests", FontAwesomeIcon.Tags, interestWidth, height))
        { directoryInterestSearch = ""; ImGui.OpenPopup("##directoryTags"); }
        DrawDirectoryInterestPicker(width);
        var lookingX = width >= interestWidth + lookingWidth + gap ? interestWidth + gap : 0;
        var lookingY = lookingX > 0 ? 0 : height + gap;
        ImGui.SetCursorScreenPos(origin + new Vector2(lookingX, lookingY));
        if (DirectoryFilterButton("directoryLookingFilter", directoryLookingFilter == 0 ? "Looking for" : DirectoryLookingFilters[directoryLookingFilter], FontAwesomeIcon.Users, lookingWidth, height))
            ImGui.OpenPopup("##directoryLookingChoices");
        if (DirectoryFilterChoices("##directoryLookingChoices", DirectoryLookingFilters, ref directoryLookingFilter, "Looking for")) MarkDirectoryDirty();
        var secondY = singleRow ? 0 : lookingY + height + gap;
        var onlineX = singleRow ? interestWidth + lookingWidth + gap * 2 : 0;
        using (ImRaii.PushStyle(ImGuiStyleVar.FramePadding, UiVec(3, 3)).Push(ImGuiStyleVar.FrameRounding, Ui(4)))
        {
            var boxHeight = ImGui.GetFontSize() + Ui(6);
            ImGui.SetCursorScreenPos(origin + new Vector2(onlineX, secondY + MathF.Max(0, (height - boxHeight) * .5f)));
            if (ImGui.Checkbox("Online now", ref directoryOnlineOnly)) MarkDirectoryDirty();
        }
        var sortX = width >= onlineWidth + gap + sortGroupWidth ? width - sortGroupWidth : 0;
        var sortY = sortX > 0 ? secondY : secondY + height + gap;
        ImGui.SetCursorScreenPos(origin + new Vector2(sortX, sortY));
        string[] sorting = ["Discover", "Shared interests", "Recently listed"];
        if (DirectoryFilterButton("directorySortFilter", "Sort: " + sorting[directorySort], FontAwesomeIcon.Sort, sortWidth, height))
            ImGui.OpenPopup("##directorySortChoices");
        if (directorySort == 0 && ImGui.IsItemHovered())
            UiFonts.SetTooltip("Rotates profile order daily to give different people visibility. Not ranked by popularity.");
        if (DirectoryFilterChoices("##directorySortChoices", sorting, ref directorySort)) MarkDirectoryDirty();
        ImGui.SetCursorScreenPos(origin + new Vector2(sortX + sortWidth + gap, sortY));
        using (UiDisabled.Scope(directoryBusy || directoryDirty))
            if (AlphaChatButton("directoryRefresh", "", FontAwesomeIcon.SyncAlt, height, height)) MarkDirectoryDirty();
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled)) UiFonts.SetTooltip("Refresh directory");
        ImGui.SetCursorScreenPos(origin + new Vector2(0, MathF.Max(secondY, sortY) + height + Ui(12)));
        if (directorySort == 1 && (directoryOwn?.Tags.Length ?? 0) == 0)
            DrawSocialHelperWrapped("Add tags to your listing to find shared interests.");
    }
    private void DrawDirectoryInterestPicker(float pageWidth)
    {
        if (!ImGui.IsPopupOpen("##directoryTags")) return;
        var popupSize = new Vector2(MathF.Min(Ui(440), MathF.Max(Ui(260), pageWidth)),
            MathF.Min(Ui(400), ImGui.GetWindowViewport().WorkSize.Y * .65f));
        ImGui.SetNextWindowSize(popupSize, ImGuiCond.Always);
        using var style = ImRaii.PushStyle(ImGuiStyleVar.WindowPadding, UiVec(12, 12))
            .Push(ImGuiStyleVar.FramePadding, UiVec(8, 5)).Push(ImGuiStyleVar.FrameRounding, Ui(7))
            .Push(ImGuiStyleVar.ItemSpacing, UiVec(7, 7));
        using var popup = UiFonts.Window(ImGui.GetFontSize(), ImRaii.Popup("##directoryTags"));
        if (!popup) return;
        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##directoryInterestSearch", "Search interests…", ref directoryInterestSearch, 40);
        using (ImRaii.PushStyle(ImGuiStyleVar.WindowPadding, Vector2.Zero))
        using (ImRaii.PushColor(ImGuiCol.ChildBg, Vector4.Zero))
        using (var list = UiFonts.Window(ImGui.GetFontSize(), ImRaii.Child("##directoryInterestList",
            new Vector2(0, MathF.Max(Ui(90), ImGui.GetContentRegionAvail().Y - Ui(43))), false)))
        {
            if (list)
            {
                var shown = 0;
                foreach (var group in DirectorySuggestedTags)
                    shown += DrawDirectoryInterestPickerGroup(group.Category, group.Tags);
                var suggested = DirectorySuggestedTags.SelectMany(g => g.Tags).ToHashSet(StringComparer.OrdinalIgnoreCase);
                shown += DrawDirectoryInterestPickerGroup("Custom interests", directoryTags.Where(t => !suggested.Contains(t)).ToArray());
                if (shown == 0) DrawSocialHelperWrapped("No interests match your search.");
            }
        }
        var footer = ImGui.GetCursorScreenPos(); var width = ImGui.GetContentRegionAvail().X;
        if (ImGui.Button("Clear filters", UiVec(100, 28))) { directoryTags.Clear(); MarkDirectoryDirty(); }
        ImGui.SameLine(); ImGui.TextColored(MutedText, $"{directoryTags.Count}/8 selected");
        ImGui.SetCursorScreenPos(footer + new Vector2(width - Ui(70), 0));
        if (ImGui.Button("Done", UiVec(70, 28))) ImGui.CloseCurrentPopup();
    }
    private int DrawDirectoryInterestPickerGroup(string heading, string[] tags)
    {
        var matching = tags.Where(t => t.Contains(directoryInterestSearch.Trim(), StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matching.Length == 0) return 0;
        ImGui.TextColored(MutedText, heading);
        var right = ImGui.GetCursorScreenPos().X + ImGui.GetContentRegionAvail().X;
        var first = true;
        foreach (var tag in matching)
        {
            using var id = ImRaii.PushId(tag);
            var selected = directoryTags.Contains(tag, StringComparer.OrdinalIgnoreCase);
            var buttonWidth = ImGui.CalcTextSize(tag).X + Ui(18);
            if (!first && ImGui.GetItemRectMax().X + buttonWidth + Ui(7) <= right) ImGui.SameLine();
            using (UiDisabled.Scope(!selected && directoryTags.Count >= 8))
            using (ImRaii.PushColor(ImGuiCol.Button, selected ? AccentInk(.45f) : FrameBg))
                if (ImGui.Button(tag + "##interestFilter", new Vector2(buttonWidth, Ui(27)))) ToggleDirectoryFilterTag(tag);
            first = false;
        }
        ImGui.Dummy(UiVec(0, 3));
        return matching.Length;
    }
    private bool DirectoryFilterButton(string id, string label, FontAwesomeIcon icon, float width, float height)
    {
        var clicked = ActivePartyButton("##" + id, new Vector2(width, height));
        var min = ImGui.GetItemRectMin(); var draw = ImGui.GetWindowDrawList();
        var fontSize = ImGui.GetFontSize(); var textY = (height - fontSize) * .5f;
        UiFonts.DrawIcon(draw, fontSize, min + new Vector2(Ui(12), textY), ImGui.GetColorU32(AccentHover), icon.ToIconString());
        draw.AddText(min + new Vector2(Ui(36), textY), ImGui.GetColorU32(Vector4.One), FitPartyTextToWidth(label, width - Ui(62)));
        UiFonts.DrawIcon(draw, fontSize * .75f, min + new Vector2(width - Ui(19), (height - fontSize * .75f) * .5f),
            ImGui.GetColorU32(MutedText), FontAwesomeIcon.ChevronDown.ToIconString());
        if (ImGui.IsItemHovered()) UiFonts.SetTooltip(label);
        return clicked;
    }
    private bool DirectoryFilterChoices(string id, string[] options, ref int selected, string? heading = null)
    {
        using var padding = ImRaii.PushStyle(ImGuiStyleVar.WindowPadding, UiVec(10, 10)).Push(ImGuiStyleVar.ItemSpacing, UiVec(8, 5));
        using var popup = ImRaii.Popup(id);
        if (!popup) return false;
        if (heading is not null)
        {
            ImGui.TextColored(MutedText, heading);
            ImGui.Separator();
            ImGui.Spacing();
        }
        var changed = false;
        for (var i = 0; i < options.Length; i++)
            if (ImGui.Selectable(options[i], selected == i)) { selected = i; changed = true; }
        return changed;
    }
    private void DrawDirectoryFilterSummary(string count)
    {
        var origin = ImGui.GetCursorScreenPos(); var width = ImGui.GetContentRegionAvail().X;
        var countWidth = ImGui.CalcTextSize(count).X;
        var inline = width >= Ui(500);
        var chipRight = inline ? MathF.Max(Ui(140), width - countWidth - Ui(20)) : width;
        var x = 0f; var y = 0f;
        using var spacing = ImRaii.PushStyle(ImGuiStyleVar.FramePadding, UiVec(10, 5)).Push(ImGuiStyleVar.FrameRounding, Ui(8));
        foreach (var tag in directoryTags.ToArray())
        {
            var label = tag + " ×"; var chipWidth = ImGui.CalcTextSize(label).X + Ui(20);
            if (x > 0 && x + chipWidth > chipRight) { x = 0; y += Ui(36); }
            ImGui.SetCursorScreenPos(origin + new Vector2(x, y));
            using (ImRaii.PushColor(ImGuiCol.Button, AccentInk(.22f)))
                if (ImGui.Button(label + "##selectedDirectoryTag", new Vector2(chipWidth, Ui(28)))) ToggleDirectoryFilterTag(tag);
            x += chipWidth + Ui(8);
        }
        if (directoryTags.Count > 0)
        {
            if (x + Ui(55) > chipRight) { x = 0; y += Ui(36); }
            ImGui.SetCursorScreenPos(origin + new Vector2(x, y));
            using (ImRaii.PushColor(ImGuiCol.Button, Vector4.Zero).Push(ImGuiCol.Text, AccentHover))
                if (ImGui.Button("Clear##directoryClear", UiVec(55, 28))) { directoryTags.Clear(); MarkDirectoryDirty(); }
        }
        var countY = directoryTags.Count > 0 ? y + Ui(36) : Ui(5);
        ImGui.SetCursorScreenPos(origin + new Vector2(0, countY));
        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + MathF.Max(Ui(80), width - countWidth - Ui(20)));
        DrawSocialHelperWrapped("Only people who choose to list their profile appear here. Online status is optional.");
        ImGui.PopTextWrapPos();
        var helperBottom = ImGui.GetItemRectMax().Y - origin.Y;
        ImGui.SetCursorScreenPos(origin + new Vector2(MathF.Max(0, width - countWidth), countY));
        ImGui.TextColored(MutedText, count);
        ImGui.SetCursorScreenPos(origin + new Vector2(0, MathF.Max(helperBottom, countY + ImGui.GetTextLineHeight()) + Ui(14)));
    }
    private void ToggleDirectoryFilterTag(string tag)
    {
        var existing = directoryTags.FindIndex(t => string.Equals(t, tag, StringComparison.OrdinalIgnoreCase));
        if (existing >= 0) directoryTags.RemoveAt(existing);
        else if (directoryTags.Count < 8) directoryTags.Add(tag);
        MarkDirectoryDirty();
    }
    private sealed record DirectoryTagPlacement(string Tag, Vector2 Position, Vector2 Size);
    private sealed record DirectoryPersonLayout(float Height, float IntroductionHeight, float JoinY, float LookingY, float TagsY, DirectoryTagPlacement[] Tags);
    private DirectoryPersonLayout MeasureDirectoryPerson(FriendDirectoryPerson person, float width, bool own)
    {
        var contentWidth = MathF.Max(Ui(40), width - Ui(32));
        var intro = string.IsNullOrEmpty(person.Introduction) ? "Happy to meet people on Alpha Channel." : person.Introduction;
        var introHeight = MathF.Max(ImGui.GetTextLineHeight(), ImGui.CalcTextSize(intro, false, contentWidth).Y);
        var y = Ui(86) + introHeight + Ui(12);
        var joinY = person.PublicPartyHostId is not null ? y : -1;
        if (joinY >= 0) y += ImGui.GetTextLineHeight() + Ui(8);
        var lookingY = y; var tagsY = y + ImGui.GetTextLineHeight() + Ui(12);
        var tags = new List<DirectoryTagPlacement>(); var x = 0f; var tagY = 0f;
        foreach (var tag in person.Tags.Take(8))
        {
            var tagWidth = MathF.Min(contentWidth, ImGui.CalcTextSize(tag).X + Ui(16));
            if (x > 0 && x + tagWidth > contentWidth) { x = 0; tagY += Ui(34); }
            tags.Add(new DirectoryTagPlacement(tag, new Vector2(x, tagY), new Vector2(tagWidth, Ui(28))));
            x += tagWidth + Ui(6);
        }
        var tagsHeight = tags.Count == 0 ? 0 : tagY + Ui(28);
        return new DirectoryPersonLayout(tagsY + tagsHeight + Ui(66), introHeight, joinY, lookingY, tagsY, tags.ToArray());
    }
    private void DrawDirectoryPerson(CharacterSession session, FriendDirectoryPerson person, float width, float height, DirectoryPersonLayout layout)
    {
        using var card = UiFonts.Window(ImGui.GetFontSize(), ImRaii.Child("##directoryPerson", new Vector2(width, height), true,
            ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse));
        if (!card) return;
        var own = person.AccountId == session.AccountId;
        var origin = ImGui.GetWindowPos(); var draw = ImGui.GetWindowDrawList();
        DrawAvatarAt(origin + UiVec(16, 16), person.AvatarIcon, person.AvatarColorHex, Ui(52), person.AvatarImageUrl);
        var supporter = PatreonBadgeTier(person.AccountId) > 0;
        var heartPosition = Vector2.Zero;
        using (UiFonts.PushRelative(1.25f, emphasis: true))
        {
            var name = FitPartyTextToWidth(person.DisplayName, width - Ui(130) - (supporter ? Ui(26) : 0));
            DrawUsernameAt(draw, origin + UiVec(82, 18), name, Vector4.One, person.AccountId);
            heartPosition = origin + UiVec(82, 18) + new Vector2(ImGui.CalcTextSize(name).X + Ui(6), 0);
        }
        var online = own ? stream.IsConnected : person.ShowOnline && person.Online;
        var statusKnown = own || person.ShowOnline;
        var status = own ? (online ? "You · Online" : "You") : person.PublicPartyHostId is not null
            ? "Hosting a Watch Party" : online ? "Online" : statusKnown ? "Offline" : "Status hidden";
        draw.AddText(origin + UiVec(82, 46), ImGui.GetColorU32(online ? Good : MutedText), status);
        var marker = origin + UiVec(62, 62);
        draw.AddCircleFilled(marker, Ui(7), ImGui.GetColorU32(FrameBg));
        draw.AddCircleFilled(marker, Ui(4.5f), ImGui.GetColorU32(online ? Good : MutedText));
        ImGui.SetCursorScreenPos(origin + UiVec(12, 12));
        if (ImGui.InvisibleButton("##directoryProfileHeader", new Vector2(width - Ui(58), Ui(60)))) OpenProfilePage(session, person.AccountId, person.DisplayName);
        if (ImGui.IsItemHovered())
        {
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
            UiFonts.SetTooltip(supporter && ImGui.IsMouseHoveringRect(heartPosition, heartPosition + UiVec(23, 23))
                ? "Patreon Supporter" : ImGui.IsMouseHoveringRect(marker - UiVec(8, 8), marker + UiVec(8, 8))
                ? online ? "Online" : statusKnown ? "Offline" : "Online status hidden" : "View Profile");
        }
        if (supporter)
        {
            ImGui.SetCursorScreenPos(heartPosition);
            // The encompassing profile header owns this hover tooltip.
            using (UiFonts.PushRelative(1.25f, emphasis: true)) DrawAlphaChatSupporterHeart(showTooltip: false);
        }
        if (!own)
        {
            ImGui.SetCursorScreenPos(origin + new Vector2(width - Ui(40), Ui(14)));
            if (AlphaChatButton("directoryActions", "", FontAwesomeIcon.EllipsisH, Ui(28), Ui(28))) ImGui.OpenPopup("##directoryPersonMenu");
            using (var menu = ImRaii.Popup("##directoryPersonMenu")) if (menu)
            {
                if (ImGui.MenuItem("Report listing")) { directoryReportTarget = person; directoryReportNote = ""; directoryEditorError = null; }
                if (ImGui.MenuItem("Block user")) QueueFriendBlockConfirmation(person.AccountId, person.DisplayName);
#if DEBUG
                if (CanAdminEvents && ImGui.MenuItem("Hide listing · Admin")) _ = ModerateDirectoryPerson(session, person);
#endif
            }
        }
        ImGui.SetCursorScreenPos(origin + UiVec(16, 86));
        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + width - Ui(32));
        ImGui.TextUnformatted(person.Introduction.Length == 0 ? "Happy to meet people on Alpha Channel." : person.Introduction);
        ImGui.PopTextWrapPos();
        if (layout.JoinY >= 0 && person.PublicPartyHostId is { } hostId)
        {
            ImGui.SetCursorScreenPos(origin + new Vector2(Ui(16), layout.JoinY));
            var blocked = stream.WatchPartyJoinBlockReason;
            var alreadyViewing = stream.Mode == StreamMode.Viewing && stream.HostId == hostId;
            if (own)
            {
                ImGui.TextColored(Good, "Join Watch Party");
                if (ImGui.IsItemHovered()) UiFonts.SetTooltip("This is your Watch Party.");
            }
            else
            {
                using (UiDisabled.Scope(blocked is not null || alreadyViewing || directoryPartyJoins.Contains(hostId)))
                {
                    var label = "Join Watch Party"; var textSize = ImGui.CalcTextSize(label);
                    var clicked = ImGui.InvisibleButton("##directoryJoinParty", textSize);
                    var min = ImGui.GetItemRectMin();
                    draw.AddText(min, ImGui.GetColorU32(Good), label);
                    if (ImGui.IsItemHovered())
                    {
                        ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
                        draw.AddLine(min + new Vector2(0, textSize.Y), min + textSize, ImGui.GetColorU32(Good));
                    }
                    if (clicked) _ = JoinDirectoryParty(session, hostId);
                }
                if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                    UiFonts.SetTooltip(blocked ?? (alreadyViewing ? "You're already in this Watch Party." : "Join this public Watch Party."));
                }
        }
        ImGui.SetCursorScreenPos(origin + new Vector2(Ui(16), layout.LookingY));
        ImGui.TextColored(MutedText, FitPartyTextToWidth("Looking for: " + person.LookingFor, width - Ui(32)));
        using (ImRaii.PushStyle(ImGuiStyleVar.FrameRounding, Ui(7)).Push(ImGuiStyleVar.FramePadding, UiVec(8, 4)))
        using (ImRaii.PushColor(ImGuiCol.Button, AccentInk(.18f)).Push(ImGuiCol.Border, AccentInk(.6f)))
        {
            foreach (var tag in layout.Tags)
            {
                using var tagId = ImRaii.PushId(tag.Tag);
                ImGui.SetCursorScreenPos(origin + new Vector2(Ui(16), layout.TagsY) + tag.Position);
                var label = FitPartyTextToWidth(tag.Tag, tag.Size.X - Ui(16));
                if (ImGui.Button(label + "##directoryInterest", tag.Size)) ToggleDirectoryFilterTag(tag.Tag);
                if (ImGui.IsItemHovered()) UiFonts.SetTooltip(tag.Tag);
            }
        }
        ImGui.SetCursorScreenPos(origin + new Vector2(Ui(16), height - Ui(50)));
        var buttonWidth = (width - Ui(42)) * .5f;
        if (AlphaChatButton("directoryProfile", "View Profile", FontAwesomeIcon.User, buttonWidth, Ui(33))) OpenProfilePage(session, person.AccountId, person.DisplayName);
        ImGui.SameLine(0, Ui(10));
        if (own)
        {
            using (UiDisabled.Scope(directoryOwnBusy || directoryMutationBusy))
                if (AlphaChatButton("directoryEditOwn", "Edit listing", FontAwesomeIcon.Edit, buttonWidth, Ui(33), primary: true))
                    _ = RefreshAndOpenDirectoryEditor(session);
            return;
        }
        var friend = friends.Any(f => f.AccountId == person.AccountId);
        var pending = friendRequests.Incoming.Concat(friendRequests.Outgoing).Any(r => r.OtherAccountId == person.AccountId)
            || partyFriendRequestsSending.ContainsKey((session.Token, person.AccountId));
        using (UiDisabled.Scope(friend || pending))
            if (AlphaChatButton("directoryAdd", friend ? "Friends" : pending ? "Pending" : "Add Friend",
                friend ? FontAwesomeIcon.UserCheck : FontAwesomeIcon.UserPlus, buttonWidth, Ui(33), primary: !friend && !pending))
                _ = SendPartyFriendRequestAsync(session, person.AccountId, person.DisplayName);
    }
    private async Task JoinDirectoryParty(CharacterSession session, string hostId)
    {
        if (!directoryPartyJoins.Add(hostId)) return;
        try
        {
            var rooms = await roomsClient.ListAsync(session.Token).ConfigureAwait(false);
            await Plugin.Framework.RunOnFrameworkThread(() =>
            {
                if (!DirectorySessionCurrent(session) || currentPage != HomePage.FriendsDirectory) return;
                var room = rooms.FirstOrDefault(r => r.HostAccountId == hostId && r.Kind is RoomKind.Public or RoomKind.Venue);
                if (room is null) { directoryError = "This Watch Party is no longer available to join. Refresh the directory."; MarkDirectoryDirty(); }
                else JoinPartyDirectoryRoom(room);
            });
        }
        catch { await Plugin.Framework.RunOnFrameworkThread(() => { if (DirectorySessionCurrent(session)) directoryError = "Couldn't check this Watch Party. Please try again."; }); }
        finally { await Plugin.Framework.RunOnFrameworkThread(() => directoryPartyJoins.Remove(hostId)); }
    }
    private async Task ModerateDirectoryPerson(CharacterSession session, FriendDirectoryPerson person)
    {
        try { await friendsDirectoryApi.Moderate(session.Token, person.AccountId, youtubeFeedToken).ConfigureAwait(false); }
        catch (Exception ex) { await Plugin.Framework.RunOnFrameworkThread(() => { if (DirectorySessionCurrent(session)) directoryError = DirectoryFailure(ex); }); return; }
        await Plugin.Framework.RunOnFrameworkThread(() => { if (DirectorySessionCurrent(session)) MarkDirectoryDirty(); });
    }
}

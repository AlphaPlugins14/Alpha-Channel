using System.Diagnostics;
using AlphaChannel.Plugin.Video;
using AlphaChannel.Plugin.YouTube;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private readonly DailymotionClient dailymotionClient = new();
    private readonly PopularVideoTracker dailymotionTracker = new();
    private readonly List<(string AccountId, PopularSubmission Submission)> dailymotionPending = [];
    private enum DailymotionPageTab { Popular, Search }
    private DailymotionPageTab dailymotionTab;
    private bool dailymotionSearchTab
    {
        get => dailymotionTab == DailymotionPageTab.Search;
        set => dailymotionTab = value ? DailymotionPageTab.Search : DailymotionPageTab.Popular;
    }
    private bool dailymotionSearching, dailymotionPopularLoading, dailymotionSubmitting;
    private string dailymotionQuery = string.Empty, dailymotionSubmittedQuery = string.Empty;
    private string? dailymotionSearchFailure, dailymotionPopularFailure, dailymotionSubmissionFailure;
    private List<VideoSearchEntry> dailymotionResults = [];
    private VideoSearchOrder dailymotionOrder;
    private VideoDurationFilter dailymotionDuration;
    private CancellationTokenSource? dailymotionSearchCancellation;
    private int dailymotionSearchGeneration;
    private bool dailymotionResetScroll;
    private double nextDailymotionSample;
    private DateTime nextDailymotionPopularUtc, nextDailymotionSubmitUtc;

    private void TickDailymotionPopular()
    {
        var now = Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
        if (now < nextDailymotionSample) return;
        nextDailymotionSample = now + .25;
        var session = CurrentSession;
        dailymotionPending.RemoveAll(p => p.AccountId != session?.AccountId);
        var id = DailymotionClient.VideoId(video.CurrentMediaUrl);
        var eligible = session is not null && Plugin.ClientState.IsLoggedIn && stream.Mode != StreamMode.Viewing && id is not null;
        var (position, duration, paused) = video.GetProgress();
        if (dailymotionTracker.Sample(eligible ? $"{session!.AccountId}:{id}:{video.PlaybackAttemptId}" : null,
                eligible && video.State == VideoPlaybackState.Playing && !paused, position, duration, video.SeekVersion, now))
        {
            if (dailymotionPending.Count >= 32) dailymotionPending.RemoveAt(0);
            dailymotionPending.Add((session!.AccountId, new(id!, Guid.NewGuid().ToString("N"), dailymotionTracker.WatchedSeconds,
                duration > 0 && double.IsFinite(duration) ? duration : null, DateTimeOffset.UtcNow.ToUnixTimeSeconds())));
        }
        if (session is not null && dailymotionPending.Count > 0 && !dailymotionSubmitting && DateTime.UtcNow >= nextDailymotionSubmitUtc)
        {
            dailymotionSubmitting = true;
            _ = SubmitDailymotionPlayAsync(session.AccountId, session.Token, dailymotionPending[0].Submission);
        }
    }

    private async Task SubmitDailymotionPlayAsync(string accountId, string token, PopularSubmission submission)
    {
        var completed = false;
        string? error = null;
        try
        {
            var result = await dailymotionClient.SubmitAsync(token, submission, youtubeFeedToken).ConfigureAwait(false);
            completed = result.Completed;
            if (!result.Accepted && !result.Duplicate) error = "Your Popular contribution could not be submitted. Temporary failures retry automatically.";
        }
        catch (OperationCanceledException) when (youtubeFeedToken.IsCancellationRequested) { return; }
        catch { error = "Your Popular contribution will be retried when the service is available."; }
        if (Volatile.Read(ref disposeStarted) != 0) return;
        await Plugin.Framework.RunOnFrameworkThread(() =>
        {
            if (Volatile.Read(ref disposeStarted) != 0) return;
            if (completed) dailymotionPending.RemoveAll(p => p.AccountId == accountId && p.Submission.EventId == submission.EventId);
            nextDailymotionSubmitUtc = DateTime.UtcNow.AddSeconds(completed ? 1 : 60);
            dailymotionSubmitting = false;
            if (CurrentSession?.AccountId == accountId) dailymotionSubmissionFailure = error;
        }).ConfigureAwait(false);
    }

    private void RefreshDailymotionPopular(bool force = false)
    {
        if (dailymotionPopularLoading || youtubeFeedToken.IsCancellationRequested || !force && DateTime.UtcNow < nextDailymotionPopularUtc) return;
        dailymotionPopularLoading = true;
        if (force) dailymotionResetScroll = true;
        nextDailymotionPopularUtc = DateTime.UtcNow.AddMinutes(5);
        _ = RefreshDailymotionPopularAsync();
    }
    private async Task RefreshDailymotionPopularAsync()
    {
        DailymotionVideo[]? results = null;
        try { results = await dailymotionClient.PopularAsync(youtubeFeedToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (youtubeFeedToken.IsCancellationRequested) { return; }
        catch { }
        if (Volatile.Read(ref disposeStarted) != 0) return;
        await Plugin.Framework.RunOnFrameworkThread(() =>
        {
            if (Volatile.Read(ref disposeStarted) != 0) return;
            if (results is not null)
            {
                Plugin.Cfg.DailymotionPopularVideos = results.ToList();
                Plugin.Cfg.Save();
                dailymotionPopularFailure = null;
            }
            else
            {
                dailymotionPopularFailure = "Popular could not be refreshed. Your last saved list is still available.";
                nextDailymotionPopularUtc = DateTime.UtcNow.AddMinutes(1);
            }
            dailymotionPopularLoading = false;
        }).ConfigureAwait(false);
    }

    private void StartDailymotionSearch()
    {
        var query = dailymotionQuery.Trim();
        if (query.Length == 0) return;
        dailymotionSearchCancellation?.Cancel();
        dailymotionSearchCancellation?.Dispose();
        dailymotionSearchCancellation = CancellationTokenSource.CreateLinkedTokenSource(youtubeFeedToken);
        var generation = ++dailymotionSearchGeneration;
        dailymotionSearchTab = true;
        dailymotionResetScroll = true;
        dailymotionSearching = true;
        dailymotionSearchFailure = null;
        dailymotionSubmittedQuery = query;
        dailymotionResults = [];
        _ = SearchDailymotionPageAsync(query, generation, dailymotionSearchCancellation.Token);
    }
    private async Task SearchDailymotionPageAsync(string query, int generation, CancellationToken token)
    {
        List<VideoSearchEntry>? results = null;
        try { results = (await dailymotionClient.SearchAsync(query, token).ConfigureAwait(false)).Select(v => v.ToEntry()).ToList(); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
        catch { }
        if (Volatile.Read(ref disposeStarted) != 0) return;
        await Plugin.Framework.RunOnFrameworkThread(() =>
        {
            if (Volatile.Read(ref disposeStarted) != 0 || token.IsCancellationRequested || generation != dailymotionSearchGeneration) return;
            dailymotionResults = results ?? [];
            dailymotionSearchFailure = results is null ? "Dailymotion search is unavailable. Please try again shortly." : null;
            dailymotionSearching = false;
        }).ConfigureAwait(false);
    }

    private void DrawDailymotionPage()
    {
        DrawDailymotionHeader();
        if (dailymotionTab == DailymotionPageTab.Popular) RefreshDailymotionPopular();
        SetGameLayoutRowGap(Ui(12));
        var available = ImGui.GetContentRegionAvail().X;
        var entries = dailymotionSearchTab ? FilterAndOrderSearchResults(dailymotionResults, dailymotionOrder, dailymotionDuration)
            : (Plugin.Cfg.DailymotionPopularVideos ?? []).Where(v => DailymotionClient.ValidId(v.VideoId)).Take(200).Select(v => v.ToEntry()).ToList();
        DrawDailymotionToolbar(entries.Count);
        var error = dailymotionSearchTab ? dailymotionSearchFailure : dailymotionPopularFailure;
        if (error is not null) using (ImRaii.TextWrapPos(0)) ImGui.TextColored(Gold, error);
        if (dailymotionSubmissionFailure is not null) using (ImRaii.TextWrapPos(0)) ImGui.TextColored(MutedText, dailymotionSubmissionFailure);
        SetGameLayoutRowGap(Ui(10));
        if (entries.Count == 0)
        {
            var loading = dailymotionSearchTab ? dailymotionSearching : dailymotionPopularLoading;
            DrawPlayerHubPanel("##dailymotionEmpty", available, Ui(!dailymotionSearchTab && !loading ? 132 : 104), false, () =>
            {
                if (!loading)
                    using (UiFonts.PushRelative(1.08f, emphasis: true))
                        ImGui.TextUnformatted(dailymotionSearchTab ? dailymotionSubmittedQuery.Length == 0 ? "Find your next watch." : "No matching videos"
                            : "Your community feed starts here.");
                using (UiFonts.PushRegularRelative(.94f))
                using (ImRaii.TextWrapPos(0))
                    ImGui.TextColored(MutedText, dailymotionSearchTab ? loading ? "Searching Dailymotion…"
                        : dailymotionSearchFailure ?? (dailymotionSubmittedQuery.Length == 0 ? "Search for a video or paste a Dailymotion link above." : "Try another search or adjust your filters.")
                        : loading ? "Loading Popular videos…" : dailymotionPopularFailure is not null ? "The shared feed is unavailable. Try refreshing shortly."
                        : "Videos appear here as people watch Dailymotion on Alpha Channel.");
                SetGameLayoutRowGap(Ui(12));
                if (!dailymotionSearchTab && !loading && GameLayoutButton("Search videos", FontAwesomeIcon.Search, Ui(160), primary: true))
                {
                    dailymotionSearchTab = true;
                    dailymotionFocusSearch = true;
                }
            });
            return;
        }
        DrawUnifiedResultsGrid(entries.Select(v => new UnifiedSearchResult(UnifiedSearchProvider.Dailymotion, v, null)).ToArray(),
            "##dailymotionResults", dailymotionResetScroll, dailymotionPage: true);
        dailymotionResetScroll = false;
    }
    private bool dailymotionFocusSearch;

    private void DrawDailymotionHeader()
    {
        DrawPlayerHubPanel("##dailymotionHeader", ImGui.GetContentRegionAvail().X, Ui(108), true, () =>
        {
            var panel = ImGui.GetWindowPos();
            var width = ImGui.GetWindowWidth() - Ui(32);
            ImGui.SetCursorScreenPos(panel + UiVec(16, 14));
            bool submitted;
            using (ImRaii.PushStyle(ImGuiStyleVar.FrameRounding, Ui(7)).Push(ImGuiStyleVar.FrameBorderSize, Ui(1))
                       .Push(ImGuiStyleVar.FramePadding, new Vector2(Ui(35), MathF.Max(1, (Ui(40) - ImGui.GetFontSize()) / 2))))
            using (ImRaii.PushColor(ImGuiCol.FrameBg, HubSurface(0x101A2D)).Push(ImGuiCol.Border, HubTint(0x395A84)))
            {
                ImGui.SetNextItemWidth(MathF.Max(1, width - Ui(88)));
                if (dailymotionFocusSearch) { ImGui.SetKeyboardFocusHere(); dailymotionFocusSearch = false; }
                submitted = ImGui.InputTextWithHint("##dailymotionQuery", "Search Dailymotion or paste a video link…", ref dailymotionQuery, 2000, ImGuiInputTextFlags.EnterReturnsTrue);
                var field = ImGui.GetItemRectMin();
                UiFonts.DrawCenteredIcon(ImGui.GetWindowDrawList(), Ui(16), field + new Vector2(Ui(17), ImGui.GetItemRectSize().Y / 2),
                    ImGui.GetColorU32(MutedText), FontAwesomeIcon.Search.ToIconString());
            }
            ImGui.SameLine(0, Ui(6));
            if (GameLayoutButton(string.Empty, FontAwesomeIcon.Search, Ui(42), primary: true, height: 40)) submitted = true;
            if (ImGui.IsItemHovered()) UiFonts.SetTooltip("Search Dailymotion");
            DrawSearchQueueButton("dailymotion", dailymotionQuery);
            if (submitted) StartDailymotionSearch();
            ImGui.SetCursorScreenPos(panel + UiVec(16, 64));
            DrawDailymotionPageTab("Popular", FontAwesomeIcon.Fire, DailymotionPageTab.Popular, width / 2, panel.Y + Ui(108));
            ImGui.SameLine(0, 0);
            DrawDailymotionPageTab("Search", FontAwesomeIcon.Search, DailymotionPageTab.Search, width / 2, panel.Y + Ui(108));
        }, contentPadding: 0);
    }

    private void DrawDailymotionToolbar(int count)
    {
        var origin = ImGui.GetCursorScreenPos();
        var width = ImGui.GetContentRegionAvail().X;
        var showFilters = dailymotionSearchTab && dailymotionSubmittedQuery.Length > 0;
        var inline = showFilters && width >= Ui(650);
        var titleWidth = width - Ui(inline ? 366 : 44);
        var title = dailymotionSearchTab ? dailymotionSubmittedQuery.Length == 0 ? "Search Dailymotion"
            : $"Results for “{dailymotionSubmittedQuery}” ({count} {(count == 1 ? "video" : "videos")})"
            : $"Popular on Alpha Channel ({count} {(count == 1 ? "video" : "videos")})";
        ImGui.SetCursorScreenPos(origin + UiVec(0, 5));
        using (UiFonts.PushRelative(1.08f, emphasis: true)) ImGui.TextUnformatted(TruncateToWidth(title, MathF.Max(1, titleWidth)));
        if (ImGui.IsItemHovered()) UiFonts.SetTooltip(title);
        ImGui.SetCursorScreenPos(origin + new Vector2(width - Ui(30), 0));
        using (UiDisabled.Scope(dailymotionSearchTab ? dailymotionSearching || dailymotionSubmittedQuery.Length == 0 : dailymotionPopularLoading))
            if (GameLayoutButton(string.Empty, FontAwesomeIcon.SyncAlt, Ui(30), height: 30))
            {
                if (dailymotionSearchTab) StartDailymotionSearch();
                else RefreshDailymotionPopular(true);
            }
        if (ImGui.IsItemHovered()) UiFonts.SetTooltip("Refresh");
        var bottom = Ui(30);
        if (showFilters)
        {
            var filterWidth = inline ? Ui(154) : MathF.Max(1, (width - Ui(10)) / 2);
            ImGui.SetCursorScreenPos(origin + new Vector2(inline ? width - Ui(354) : 0, inline ? 0 : Ui(38)));
            using (ImRaii.PushStyle(ImGuiStyleVar.FrameRounding, Ui(6)).Push(ImGuiStyleVar.FrameBorderSize, Ui(1))
                       .Push(ImGuiStyleVar.FramePadding, new Vector2(Ui(9), MathF.Max(1, (Ui(30) - ImGui.GetFontSize()) / 2))))
            using (ImRaii.PushColor(ImGuiCol.FrameBg, HubSurface(0x101A2D)).Push(ImGuiCol.Border, HubTint(0x395A84)))
            {
                ImGui.SetNextItemWidth(filterWidth);
                if (ImGui.BeginCombo("##dailymotionOrder", "Order: " + GetVideoSearchOrderName(dailymotionOrder)))
                {
                    foreach (var order in Enum.GetValues<VideoSearchOrder>())
                        if (ImGui.Selectable(GetVideoSearchOrderName(order), dailymotionOrder == order)) { dailymotionOrder = order; dailymotionResetScroll = true; }
                    ImGui.EndCombo();
                }
                ImGui.SameLine(0, Ui(10));
                ImGui.SetNextItemWidth(filterWidth);
                if (ImGui.BeginCombo("##dailymotionDuration", "Duration: " + GetVideoDurationFilterName(dailymotionDuration)))
                {
                    foreach (var filter in Enum.GetValues<VideoDurationFilter>())
                        if (ImGui.Selectable(GetVideoDurationFilterName(filter), dailymotionDuration == filter)) { dailymotionDuration = filter; dailymotionResetScroll = true; }
                    ImGui.EndCombo();
                }
            }
            if (!inline) bottom = Ui(68);
        }
        ImGui.SetCursorScreenPos(origin);
        ImGui.Dummy(new(width, bottom));
    }

    private void DrawDailymotionPageTab(string label, FontAwesomeIcon icon, DailymotionPageTab tab, float width, float bottom)
    {
        var origin = ImGui.GetCursorScreenPos();
        if (ImGui.InvisibleButton("##dailymotionTab" + label, new(width, Ui(44))))
        {
            dailymotionTab = tab;
            dailymotionResetScroll = true;
        }
        var draw = ImGui.GetWindowDrawList();
        if (ImGui.IsItemHovered())
        {
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
            draw.AddRectFilled(origin, origin + new Vector2(width, Ui(42)), ImGui.GetColorU32(AccentFieldBg), Ui(5));
        }
        var active = dailymotionTab == tab;
        DrawPlayerActionButtonContent(origin, new(width, Ui(40)), icon, label, active ? AccentHover : MutedText);
        if (active) draw.AddRectFilled(new Vector2(origin.X, bottom - Ui(3)), new Vector2(origin.X + width, bottom), ImGui.GetColorU32(Accent));
    }
}

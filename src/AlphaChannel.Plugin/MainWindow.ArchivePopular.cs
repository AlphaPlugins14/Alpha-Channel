using System.Diagnostics;
using AlphaChannel.Plugin.Video;
using AlphaChannel.Plugin.YouTube;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
namespace AlphaChannel.Plugin;
internal sealed partial class MainWindow
{
    private readonly ArchivePopularityClient archiveClient = new();
    private readonly PopularVideoTracker archiveTracker = new();
    private readonly List<(string AccountId, ArchivePopularSubmission Submission)> archivePending = [];
    private bool internetArchivePopularTab = true, archiveSubmitting, archivePopularLoading;
    private string? archiveSubmissionFailure, archivePopularFailure;
    private double nextArchiveSample;
    private DateTime nextArchiveSubmitUtc, nextArchivePopularUtc;
    private string[] archivePopularIds = [];
    private int archivePopularVisible = 24;
    private void TickArchivePopular()
    {
        var now = Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
        if (now < nextArchiveSample) return;
        nextArchiveSample = now + .25;
        var session = CurrentSession;
        archivePending.RemoveAll(p => p.AccountId != session?.AccountId);
        var identity = ArchivePopularityClient.Identity(video.CurrentMediaUrl);
        if (identity is null && ArchivePopularityClient.IsArchiveUrl(video.CurrentMediaUrl))
            identity = ArchivePopularityClient.Identity(video.CurrentResolvedMediaUrl);
        var id = identity?.Id;
        var eligible = session is not null && Plugin.ClientState.IsLoggedIn && stream.Mode != StreamMode.Viewing && id is not null;
        var (position, duration, paused) = video.GetProgress();
        if (archiveTracker.Sample(eligible ? $"{session!.AccountId}:{id}:{identity?.Episode}:{video.PlaybackAttemptId}" : null,
                eligible && video.State == VideoPlaybackState.Playing && !paused, position, duration, video.SeekVersion, now))
        {
            if (archivePending.Count >= 32) archivePending.RemoveAt(0);
            archivePending.Add((session!.AccountId, new(id!, identity!.Value.Episode, Guid.NewGuid().ToString("N"), archiveTracker.WatchedSeconds,
                duration > 0 && double.IsFinite(duration) ? duration : null, DateTimeOffset.UtcNow.ToUnixTimeSeconds())));
        }
        if (session is not null && archivePending.Count > 0 && !archiveSubmitting && DateTime.UtcNow >= nextArchiveSubmitUtc)
        {
            archiveSubmitting = true;
            _ = SubmitArchivePlayAsync(session.AccountId, session.Token, archivePending[0].Submission);
        }
    }

    private async Task SubmitArchivePlayAsync(string accountId, string token, ArchivePopularSubmission submission)
    {
        var completed = false;
        string? error = null;
        try
        {
            var result = await archiveClient.SubmitAsync(token, submission, youtubeFeedToken).ConfigureAwait(false);
            completed = result.Completed;
            if (!result.Accepted && !result.Duplicate) error = completed ? "Your Popular contribution could not be submitted." : "Your Popular contribution will retry automatically.";
        }
        catch (OperationCanceledException) when (youtubeFeedToken.IsCancellationRequested) { return; }
        catch { error = "Your Popular contribution will be retried when the service is available."; }
        if (Volatile.Read(ref disposeStarted) != 0) return;
        await Plugin.Framework.RunOnFrameworkThread(() =>
        {
            if (Volatile.Read(ref disposeStarted) != 0) return;
            if (completed) archivePending.RemoveAll(p => p.AccountId == accountId && p.Submission.EventId == submission.EventId);
            nextArchiveSubmitUtc = DateTime.UtcNow.AddSeconds(completed ? 1 : 60);
            archiveSubmitting = false;
            if (CurrentSession?.AccountId == accountId) archiveSubmissionFailure = error;
        }).ConfigureAwait(false);
    }


    private void RefreshArchivePopular(bool force = false)
    {
        if (archivePopularLoading || youtubeFeedToken.IsCancellationRequested || !force && DateTime.UtcNow < nextArchivePopularUtc) return;
        archivePopularLoading = true;
        nextArchivePopularUtc = DateTime.UtcNow.AddMinutes(5);
        _ = LoadArchivePopularAsync(refreshFeed: true);
    }
    private async Task LoadArchivePopularAsync(bool refreshFeed)
    {
        string[]? ids = null;
        InternetArchiveItem[]? items = null;
        try
        {
            ids = refreshFeed ? await archiveClient.PopularAsync(youtubeFeedToken).ConfigureAwait(false) : archivePopularIds;
            internetArchive.SeedResolvedItems(Plugin.Cfg.ArchivePopularItems ?? []);
            var resolved = await Task.WhenAll(ids.Take(archivePopularVisible).Select(async id =>
            {
                try { return await internetArchive.ResolveIdentifierAsync(id, youtubeFeedToken).ConfigureAwait(false); }
                catch (OperationCanceledException) when (youtubeFeedToken.IsCancellationRequested) { throw; }
                catch { return null; }
            })).ConfigureAwait(false);
            // Preserve last-known metadata when Archive temporarily cannot resolve an item.
            var saved = (Plugin.Cfg.ArchivePopularItems ?? []).DistinctBy(i => i.Identifier).ToDictionary(i => i.Identifier);
            items = ids.Take(archivePopularVisible).Select((id, index) => resolved[index] ?? saved.GetValueOrDefault(id)).OfType<InternetArchiveItem>().ToArray();
            if (items.Length == 0 && ids.Length > 0) throw new InvalidDataException("Archive metadata unavailable.");
        }
        catch (OperationCanceledException) when (youtubeFeedToken.IsCancellationRequested) { return; }
        catch { items = null; }
        if (Volatile.Read(ref disposeStarted) != 0) return;
        await Plugin.Framework.RunOnFrameworkThread(() =>
        {
            if (Volatile.Read(ref disposeStarted) != 0) return;
            if (items is not null && ids is not null)
            {
                archivePopularIds = ids;
                Plugin.Cfg.ArchivePopularItems = items.ToList();
                Plugin.Cfg.Save();
                archivePopularFailure = null;
            }
            else
            {
                archivePopularFailure = "Popular could not be refreshed. Your last saved list is still available.";
                nextArchivePopularUtc = DateTime.UtcNow.AddMinutes(1);
            }
            archivePopularLoading = false;
        }).ConfigureAwait(false);
    }
    private void DrawArchivePopular()
    {
        RefreshArchivePopular();
        var items = (Plugin.Cfg.ArchivePopularItems ?? []).Where(i => ArchivePopularityClient.ValidId(i.Identifier)).Take(200).ToArray();
        using (UiFonts.PushRelative(1.08f, emphasis: true))
            ImGui.TextUnformatted($"Popular on Alpha Channel ({(archivePopularIds.Length > 0 ? archivePopularIds.Length : items.Length)} videos)");
        if (archivePopularFailure is not null) using (ImRaii.TextWrapPos(0)) ImGui.TextColored(Gold, archivePopularFailure);
        if (archiveSubmissionFailure is not null) using (ImRaii.TextWrapPos(0)) ImGui.TextColored(MutedText, archiveSubmissionFailure);
        SetGameLayoutRowGap(Ui(10));
        if (items.Length > 0)
        {
            DrawInternetArchiveGrid(items);
            if (archivePopularIds.Length > archivePopularVisible)
                using (UiDisabled.Scope(archivePopularLoading))
                    if (GameLayoutButton("Show more", FontAwesomeIcon.Plus, Ui(160)))
                    {
                        archivePopularVisible += 24;
                        archivePopularLoading = true;
                        _ = LoadArchivePopularAsync(refreshFeed: false);
                    }
        }
        else DrawPlayerHubPanel("##archivePopularEmpty", ImGui.GetContentRegionAvail().X, Ui(112), false, () =>
        {
            using (UiFonts.PushRelative(1.08f, emphasis: true)) ImGui.TextUnformatted(archivePopularLoading ? "Loading Popular videos…" : "Your community feed starts here.");
            using (ImRaii.TextWrapPos(0)) ImGui.TextColored(MutedText, "Videos appear here as people watch Internet Archive on Alpha Channel.");
        });
    }
}

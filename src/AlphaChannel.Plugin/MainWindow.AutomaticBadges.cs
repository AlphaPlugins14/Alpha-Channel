using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AlphaChannel.Plugin.Auth;
using AlphaChannel.Plugin.Video;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private ActivityBadgeTracker activityBadgeTracker = new();
    private double nextHostingBadgeCheck, nextCommunityBadgeCheck;
    private int lastHostingBadgeTarget;
    private HashSet<string> CompletedActivityBadges(string account)
    {
        if (!Plugin.Cfg.ActivityBadgesCompleted.TryGetValue(account, out var ids))
            Plugin.Cfg.ActivityBadgesCompleted[account] = ids = new(StringComparer.Ordinal);
        return ids;
    }
    private string? badgeSavedActivity, badgeSavedMovie;
    private MovieBuffTracker movieBuffTracker = new();
    private string? automaticBadgeAccount, automaticBadgeToken;
    private bool automaticBadgeBusy;
    private int automaticBadgeGeneration;
    private double nextBadgeSample, nextBadgeRetry, nextMovieProgressSave;
    private static readonly byte[] MovieProgressEntropy = Encoding.UTF8.GetBytes("AlphaChannel.MovieBuff.Progress.v1");

    internal void TickAutomaticBadges()
    {
        if (Volatile.Read(ref disposeStarted) != 0) return;
        var now = Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
        if (now < nextBadgeSample) return;
        nextBadgeSample = now + 1;
        var session = Plugin.ClientState.IsLoggedIn ? CurrentSession : null;
        if (automaticBadgeAccount != session?.AccountId || automaticBadgeToken != session?.Token)
        {
            SaveBadgeProgress();
            badgeSavedActivity = badgeSavedMovie = null;
            automaticBadgeAccount = session?.AccountId;
            automaticBadgeToken = session?.Token;
            automaticBadgeGeneration++;
            halloweenBadgePopupOpen = false;
            automaticBadgeBusy = false;
            nextBadgeRetry = nextHostingBadgeCheck = nextCommunityBadgeCheck = 0;
            lastHostingBadgeTarget = 0;
            movieBuffTracker = new();
            RestoreMovieBuffProgress();
            activityBadgeTracker = new();
            RestoreActivityBadgeProgress();
            badgeSavedActivity = JsonSerializer.Serialize(activityBadgeTracker);
            badgeSavedMovie = movieBuffTracker.Progress.Count == 0 ? null : JsonSerializer.Serialize(movieBuffTracker.Progress);
            nextMovieProgressSave = now + 60;
        }
        if (session is null) return;
        var accountId = session.AccountId;
        if (!Plugin.Cfg.MovieBuffBadgeCompleted.Contains(accountId))
        {
            var identity = MovieBuffMediaIdentity();
            var (position, _, paused) = video.GetProgress();
            movieBuffTracker.Sample(identity,
                identity is not null && video.State == VideoPlaybackState.Playing && !paused,
                position, video.SeekVersion, now);
        }
        if (stream.Mode == StreamMode.Viewing && stream.HostId is { } partyHost &&
            stream.CurrentRoomState?.HostId == partyHost)
        {
            var before = activityBadgeTracker.Parties.Count;
            activityBadgeTracker.Join(accountId, partyHost, ActivityBadgeTracker.ReadParty(stream.CurrentRoomState.Reason) ?? Guid.Empty.ToString("D"));
            if (before != activityBadgeTracker.Parties.Count) SaveBadgeProgress();
        }
        string? emulator = screenController.Engine.IsPlayingSnes ? "snes" : screenController.Engine.IsPlayingGameBoy ? "gb" :
            screenController.Engine.IsPlayingNes ? "nes" : screenController.Engine.IsPlayingGameBoyAdvance ? "gba" :
            screenController.Engine.IsPlayingMasterSystem ? "sms" : screenController.Engine.IsPlayingGameGear ? "gg" : null;
        activityBadgeTracker.Sample(emulator, ActiveActivity.ToString(),
            (studioSessionActive && studioBroadcastRequested && studioPublished &&
                StudioState.BroadcastState == "LIVE" && studioAccount == accountId) ||
            (djIsOnAir && !studioSessionActive && djCredentialsAccountId == accountId), now);
        if (now >= nextMovieProgressSave)
        {
            SaveBadgeProgress();
            nextMovieProgressSave = now + 60;
        }
        if (automaticBadgeBusy || now < nextBadgeRetry) return;
        string? check = Plugin.Cfg.Halloween2026BadgePending.Contains(accountId) ? "halloween-completed" :
            !Plugin.Cfg.InitialBadgeChecksCompleted.Contains(accountId) ? "initial" :
            now >= nextCommunityBadgeCheck && friendsAccountId == accountId && friends.Length >= 10 &&
            !Plugin.Cfg.CommunityBadgeCompleted.Contains(accountId) ? "community" :
            !Plugin.Cfg.MovieBuffBadgeCompleted.Contains(accountId) && movieBuffTracker.Eligible ? "movie" :
            activityBadgeTracker.Eligible().FirstOrDefault(id => !CompletedActivityBadges(accountId).Contains(id));
        if (check is null && stream.Mode == StreamMode.Hosting)
        {
            var viewers = stream.Roster.Where(p => !string.Equals(p.UserId, accountId, StringComparison.OrdinalIgnoreCase))
                .Select(p => p.UserId).Distinct(StringComparer.OrdinalIgnoreCase).Count();
            var completed = CompletedActivityBadges(accountId);
            var target = viewers >= 20 && !completed.Contains("big-premiere") ? 20 :
                viewers >= 10 && !completed.Contains("sofa-squad") ? 10 : 0;
            if (target > 0 && (now >= nextHostingBadgeCheck || target > lastHostingBadgeTarget))
            {
                lastHostingBadgeTarget = target;
                check = "hosting";
            }
        }
        if (check is null) return;
        automaticBadgeBusy = true;
        _ = RequestAutomaticBadgeAsync(accountId, session.Token, check, automaticBadgeGeneration);
    }

    private void CompleteHalloweenBadge()
    {
        if (CurrentSession is not { } account || Plugin.Cfg.Halloween2026BadgeCompleted.Contains(account.AccountId)) return;
        // Only called after 60 seconds of decoded local show playback. Party membership/countdown is insufficient.
        Plugin.Cfg.Halloween2026BadgePending.Add(account.AccountId);
        Plugin.Cfg.Save();
        nextBadgeRetry = 0;
    }

    private string? MovieBuffMediaIdentity()
    {
        if (!video.HasVideoPlaybackSession || video.IsAudioOnly || video.IsPlayingGame ||
            video.IsPlayingBrowser || video.IsPlayingWhiteboard || video.IsPlayingImage) return null;
        var url = video.CurrentMediaUrl;
        if (string.IsNullOrWhiteSpace(url) || LiveDjStream.AppliesTo(url)) return null;
        var source = queue.Current?.Source ?? "";
        if (source.Contains("Music", StringComparison.OrdinalIgnoreCase) ||
            source.Contains("Radio", StringComparison.OrdinalIgnoreCase) ||
            source.Contains("DJ", StringComparison.OrdinalIgnoreCase) ||
            source.Equals("Gameplay", StringComparison.OrdinalIgnoreCase)) return null;
        string? identity = null;
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            if (uri.Host.Equals("youtu.be", StringComparison.OrdinalIgnoreCase) ||
                uri.Host.Equals("youtube.com", StringComparison.OrdinalIgnoreCase) ||
                uri.Host.EndsWith(".youtube.com", StringComparison.OrdinalIgnoreCase))
                identity = "youtube:" + GetYouTubeVideoId(url);
            else if (DailymotionClient.VideoId(url) is { } dm)
                identity = "dailymotion:" + dm;
            else if (ArchivePopularityClient.Identity(url) is { } archive)
                identity = "archive:" + archive.Id + "\n" + archive.Episode;
            else if (url.Contains("ac_content=local-video", StringComparison.OrdinalIgnoreCase))
                identity = url + "\n" + stream.CurrentRoomState?.MediaTitle;
            else if (!Plugin.IsLiveBroadcastUrl(url) &&
                (uri.IsFile || Path.GetExtension(uri.AbsolutePath).ToLowerInvariant() is
                    ".mp4" or ".mkv" or ".webm" or ".avi" or ".mov" or ".m4v"))
                identity = url;
        }
        else if (video.IsPlayingLocalVideo) identity = url;
        return identity is null ? null : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
    }

    private async Task RequestAutomaticBadgeAsync(string accountId, string token, string check, int generation)
    {
        var succeeded = false;
        try
        {
            var cosmetic = check is "halloween-completed" or "movie" or "chat-buddies" or "controller-crunch" or "arcade-buddy" or "vinyl-pal";
            var badges = cosmetic
                ? await profileBadgeWorker.ClaimCosmeticAsync(token, accountId, check == "halloween-completed" ? "haunted-tape" : check == "movie" ? "popcorn-pal" : check, youtubeFeedToken)
                : await profileBadgeWorker.CheckEligibilityAsync(token, accountId, check, youtubeFeedToken);
            await Plugin.Framework.RunOnFrameworkThread(() =>
            {
                if (Volatile.Read(ref disposeStarted) != 0 || generation != automaticBadgeGeneration ||
                    CurrentSession?.AccountId != accountId || CurrentSession.Token != token) return;
                if (check == "initial") Plugin.Cfg.InitialBadgeChecksCompleted.Add(accountId);
                if (badges.Contains("haunted-tape"))
                {
                    var firstAward = Plugin.Cfg.Halloween2026BadgeCompleted.Add(accountId);
                    Plugin.Cfg.Halloween2026BadgePending.Remove(accountId);
                    if (check == "halloween-completed" && firstAward) halloweenBadgePopupOpen = true;
                }
                if (badges.Contains("heart-tv")) Plugin.Cfg.CommunityBadgeCompleted.Add(accountId);
                if (badges.Contains("popcorn-pal"))
                {
                    Plugin.Cfg.MovieBuffBadgeCompleted.Add(accountId);
                    Plugin.Cfg.MovieBuffProtectedProgress.Remove(accountId);
                    movieBuffTracker = new();
                }
                CompletedActivityBadges(accountId).UnionWith(badges.Where(id => id is
                    "sofa-squad" or "big-premiere" or "chat-buddies" or "controller-crunch" or "arcade-buddy" or "vinyl-pal"));
                if (profilePageAccountId == accountId) profilePageUnlockedBadgeIds = badges;
                Plugin.Cfg.Save();
            });
            succeeded = true;
        }
        catch (OperationCanceledException) when (youtubeFeedToken.IsCancellationRequested) { }
        catch (OperationCanceledException)
        { AepLog.Warning($"[Badges] Automatic badge check '{check}' timed out; will retry in 5 minutes."); }
        catch (Exception exception)
        {
            // Do not log exception messages or request data: those may contain
            // URLs, account information or credentials from a transport failure.
            var failure = exception is HttpRequestException { StatusCode: { } status }
                ? $"HTTP {(int)status}" : exception.GetType().Name;
            AepLog.Warning($"[Badges] Automatic badge check '{check}' failed ({failure}); will retry in 5 minutes.");
        }
        finally
        {
            await Plugin.Framework.RunOnFrameworkThread(() =>
            {
                if (Volatile.Read(ref disposeStarted) != 0 || generation != automaticBadgeGeneration) return;
                automaticBadgeBusy = false;
                var now = Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
                nextBadgeRetry = now + (succeeded ? 1 : 300);
                if (check == "hosting") nextHostingBadgeCheck = now + 300;
                if (check == "community") nextCommunityBadgeCheck = now + 300;
            });
        }
    }

    private void SaveBadgeProgress()
    {
        var account = automaticBadgeAccount;
        if (account is null) return;
        var activity = JsonSerializer.Serialize(activityBadgeTracker);
        var movie = movieBuffTracker.Progress.Count > 0 && !Plugin.Cfg.MovieBuffBadgeCompleted.Contains(account)
            ? JsonSerializer.Serialize(movieBuffTracker.Progress) : null;
        if (activity == badgeSavedActivity && movie == badgeSavedMovie) return;
        badgeSavedActivity = activity; badgeSavedMovie = movie;
        try
        {
            string Protect(string plain, byte[] entropy)
            {
                var bytes = Encoding.UTF8.GetBytes(plain);
                try { return Convert.ToBase64String(ProtectedData.Protect(bytes, entropy, DataProtectionScope.CurrentUser)); }
                finally { CryptographicOperations.ZeroMemory(bytes); }
            }
            Plugin.Cfg.ActivityBadgeProtectedProgress[account] = Protect(activity,
                SHA256.HashData(Encoding.UTF8.GetBytes(account + "\nAlphaChannel.ActivityBadges.Progress.v1")));
            if (movie is not null && !Plugin.Cfg.MovieBuffBadgeCompleted.Contains(account))
                Plugin.Cfg.MovieBuffProtectedProgress[account] = Protect(movie,
                    SHA256.HashData(Encoding.UTF8.GetBytes(account).Concat(MovieProgressEntropy).ToArray()));
            Plugin.Cfg.SavePublicStateDeferred();
        }
        catch (Exception)
        {
            badgeSavedActivity = badgeSavedMovie = null;
            AepLog.Warning("[Badges] Local activity progress could not be saved.");
        }
    }

    private byte[] ActivityProgressEntropy() => SHA256.HashData(Encoding.UTF8.GetBytes(
        automaticBadgeAccount + "\nAlphaChannel.ActivityBadges.Progress.v1"));

    private void RestoreActivityBadgeProgress()
    {
        if (automaticBadgeAccount is null || !Plugin.Cfg.ActivityBadgeProtectedProgress.TryGetValue(automaticBadgeAccount, out var blob)) return;
        try
        {
            var bytes = ProtectedData.Unprotect(Convert.FromBase64String(blob), ActivityProgressEntropy(), DataProtectionScope.CurrentUser);
            try { activityBadgeTracker = JsonSerializer.Deserialize<ActivityBadgeTracker>(bytes) ?? new(); }
            finally { CryptographicOperations.ZeroMemory(bytes); }
            activityBadgeTracker.Sanitize();
        }
        catch (Exception) { activityBadgeTracker = new(); AepLog.Warning("[Badges] Local activity progress could not be restored."); }
    }

    private void RestoreMovieBuffProgress()
    {
        if (automaticBadgeAccount is null || !Plugin.Cfg.MovieBuffProtectedProgress.TryGetValue(automaticBadgeAccount, out var blob)) return;
        try
        {
            var entropy = SHA256.HashData(Encoding.UTF8.GetBytes(automaticBadgeAccount).Concat(MovieProgressEntropy).ToArray());
            var bytes = ProtectedData.Unprotect(Convert.FromBase64String(blob), entropy, DataProtectionScope.CurrentUser);
            try
            {
                var progress = JsonSerializer.Deserialize<Dictionary<string,double>>(bytes);
                if (progress is not null) movieBuffTracker.Restore(progress);
            }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
        catch (Exception) { AepLog.Warning("[Badges] Local movie progress could not be restored."); }
    }

}

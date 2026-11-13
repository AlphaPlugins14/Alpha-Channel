using AlphaChannel.Plugin.Auth;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private readonly object patreonBadgeLock = new();
    private PatreonBadgeCache? patreonBadgeCache;
    private string? patreonBadgeViewerAccountId;
    private bool patreonBadgeLaunchPending;
    private bool patreonBadgeFriendsReady;
    private bool patreonBadgeRequestRunning;

    private static string PatreonTierName(int tier) => tier switch
    {
        1 => "Supporter",
        2 => "Film Buff",
        3 => "Movie Connoisseur",
        4 => "Venue Operator",
        5 => "Ultimate Member",
        _ => string.Empty,
    };

    private int PatreonBadgeTier(string? accountId)
    {
        if (string.IsNullOrWhiteSpace(accountId) || !Guid.TryParse(accountId, out _) ||
            CurrentSession is null)
            return 0;

        if (string.Equals(accountId, CurrentSession.AccountId, StringComparison.OrdinalIgnoreCase))
            return CurrentVerifiedPatreonTier();

        lock (patreonBadgeLock)
            return patreonBadgeViewerAccountId == CurrentSession?.AccountId
                ? patreonBadgeCache?.GetTier(accountId, DateTime.UtcNow) ?? 0 : 0;
    }

    // Callers hold the badge lock. Discovery is event-driven, never a timed roster scan.
    private void EnsurePatreonBadgeCache(CharacterSession session)
    {
        if (patreonBadgeViewerAccountId == session.AccountId && patreonBadgeCache is not null) return;
        if (!Guid.TryParse(session.AccountId, out var viewerId)) return;
        patreonBadgeCache?.Dispose();
        patreonBadgeViewerAccountId = session.AccountId;
        patreonBadgeCache = new PatreonBadgeCache(Path.Combine(
            Plugin.PluginInterface.ConfigDirectory.FullName, "PatreonBadges",
            viewerId.ToString("N") + ".json"), DateTime.UtcNow, AepLog.Warning);
        patreonBadgeLaunchPending = true;
        patreonBadgeFriendsReady = false;
        friendsDirty = true;
    }

    private void ObservePatreonPeople(IEnumerable<string> accountIds)
    {
        if (Volatile.Read(ref disposeStarted) != 0 || CurrentSession is not { } session) return;
        lock (patreonBadgeLock)
        {
            EnsurePatreonBadgeCache(session);
            patreonBadgeCache?.Observe(accountIds.Where(id =>
                !string.Equals(id, session.AccountId, StringComparison.OrdinalIgnoreCase)), DateTime.UtcNow);
        }
    }

    private void ObservePatreonFriends(string accountId, IEnumerable<string> accountIds)
    {
        if (Volatile.Read(ref disposeStarted) != 0 || CurrentSession is not { } session || session.AccountId != accountId) return;
        lock (patreonBadgeLock)
        {
            EnsurePatreonBadgeCache(session);
            var now = DateTime.UtcNow;
            patreonBadgeCache?.SetFriends(accountIds.Where(id =>
                !string.Equals(id, session.AccountId, StringComparison.OrdinalIgnoreCase)), now);
            patreonBadgeFriendsReady = true;
            patreonBadgeCache?.Maintain(now);
            if (patreonBadgeLaunchPending)
            {
                patreonBadgeLaunchPending = false;
                patreonBadgeCache?.RequestImmediate(now);
            }
        }
    }

    private void RequestPendingPatreonBadges(string accountId)
    {
        lock (patreonBadgeLock)
            if (CurrentSession?.AccountId == accountId && patreonBadgeViewerAccountId == accountId)
                patreonBadgeCache?.RequestImmediate(DateTime.UtcNow);
    }

    // Framework tick only checks deadlines. Cache enumeration happens on events or maintenance deadlines.
    internal void RefreshPatreonBadgesIfNeeded()
    {
        var session = CurrentSession;
        lock (patreonBadgeLock)
        {
            if (session is null)
            {
                patreonBadgeViewerAccountId = null;
                patreonBadgeCache?.Dispose(); patreonBadgeCache = null;
                return;
            }
            EnsurePatreonBadgeCache(session);
        }
        if (friendsDirty && !friendsLoading) RefreshFriends(session.Token);
        lock (patreonBadgeLock)
        {
            if (patreonBadgeCache is null) return;
            var now = DateTime.UtcNow;
            // Failed friend loading must not stall status checks, or prune friends
            // whose protection has not yet been reconciled (especially legacy files).
            patreonBadgeCache.Maintain(now, cleanup: patreonBadgeFriendsReady);
            if (patreonBadgeLaunchPending)
            {
                if (friendsLoading) return;
                patreonBadgeLaunchPending = false;
                patreonBadgeCache.RequestImmediate(now);
            }
            if (patreonBadgeRequestRunning) return;
            var refresh = patreonBadgeCache.BeginRefresh(now);
            if (refresh is null) return;
            patreonBadgeRequestRunning = true;
            _ = FetchPatreonBadgesAsync(patreonBadgeCache, session.AccountId, session.Token, refresh);
        }
    }

    private async Task FetchPatreonBadgesAsync(PatreonBadgeCache cache, string accountId, string token,
        PatreonBadgeCache.Refresh refresh)
    {
        await cache.FlushAsync().ConfigureAwait(false); // Persist request cooldown before contacting the Worker.
        var successes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var failures = new List<string>();
        try
        {
            foreach (var batch in refresh.AccountIds.Chunk(80))
            {
                string[] ids;
                lock (patreonBadgeLock)
                {
                    if (!ReferenceEquals(cache, patreonBadgeCache) || CurrentSession?.AccountId != accountId || CurrentSession.Token != token) return;
                    ids = batch.Where(cache.IsPending).ToArray();
                }
                if (ids.Length == 0) continue;
                try
                {
                    var badges = await patreonWorkerClient.GetBadgesAsync(token, ids, CancellationToken.None)
                        .ConfigureAwait(false);
                    foreach (var id in ids)
                        if (badges.TryGetValue(id, out var tier) && tier is >= 0 and <= 5) successes[id] = tier;
                        else failures.Add(id);
                }
                catch (Exception)
                {
                    AepLog.Warning("[Patreon] Badge lookup failed.");
                    failures.AddRange(ids);
                }
            }
        }
        finally
        {
            lock (patreonBadgeLock)
            {
                // A sign-out/reload may have opened a new cache for this same
                // account. An old request must not overwrite its saved schedule.
                if (ReferenceEquals(cache, patreonBadgeCache))
                    cache.CompleteRefresh(successes, failures, DateTime.UtcNow);
                patreonBadgeRequestRunning = false;
            }
        }
    }

    private void DrawPatreonBadgeInline(string accountId, bool compact = false)
    {
        var tier = PatreonBadgeTier(accountId);
        if (tier == 0)
            return;

        var size = PatreonBadgeSize(tier, compact);
        var previousMin = ImGui.GetItemRectMin();
        var previousMax = ImGui.GetItemRectMax();
        var origin = ImGui.GetCursorScreenPos();
        // Inline badges follow a name; align the pill with that name's line.
        origin.Y = previousMin.Y + (previousMax.Y - previousMin.Y - size.Y) * 0.5f;
        ImGui.SetCursorScreenPos(origin);
        ImGui.InvisibleButton($"##patreonBadge{accountId}{(compact ? "Mini" : "Full")}", size);
        DrawPatreonBadgeShape(origin, size, tier, compact);
        if (ImGui.IsItemHovered())
            UiFonts.SetTooltip($"Patreon tier: {PatreonTierName(tier)}");
    }

    private void DrawPatreonBadgeAt(Vector2 origin, string accountId, string uniqueId, bool compact = true)
    {
        var tier = PatreonBadgeTier(accountId);
        if (tier == 0)
            return;

        var size = PatreonBadgeSize(tier, compact);
        var savedCursor = ImGui.GetCursorScreenPos();
        ImGui.SetCursorScreenPos(origin);
        ImGui.InvisibleButton($"##patreonBadge{uniqueId}", size);
        DrawPatreonBadgeShape(origin, size, tier, compact);
        if (ImGui.IsItemHovered())
            UiFonts.SetTooltip($"Patreon tier: {PatreonTierName(tier)}");
        ImGui.SetCursorScreenPos(savedCursor);
    }

    private static Vector2 PatreonBadgeSize(int tier, bool compact)
    {
        var height = Ui(compact ? 19f : 23f);
        return new Vector2(compact
            ? Ui(28f)
            : Ui(30f) + ImGui.CalcTextSize(PatreonTierName(tier)).X + Ui(12f), height);
    }

    private static void DrawPatreonBadgeShape(Vector2 origin, Vector2 size, int tier, bool compact)
    {
        var draw = ImGui.GetWindowDrawList();
        var end = origin + size;
        draw.AddRectFilled(origin, end, ImGui.GetColorU32(new Vector4(0.17f, 0.075f, 0.105f, 1f)), size.Y / 2f);
        draw.AddRect(origin, end, ImGui.GetColorU32(PatreonOrange), size.Y / 2f,
            ImDrawFlags.None, Ui(1.2f));
        var heart = FontAwesomeIcon.Heart.ToIconString();
        var iconFontSize = ImGui.GetFontSize() * (compact ? 0.77f : 0.85f);
        using var uiIconFont9589=UiFonts.PushIcon();
        var iconSize = ImGui.CalcTextSize(heart) * (iconFontSize / ImGui.GetFontSize());
        uiIconFont9589.Dispose();
        var iconCenterX = compact ? size.X * 0.5f : Ui(15f);
        UiFonts.DrawIcon(draw, iconFontSize, origin + new Vector2(iconCenterX - iconSize.X * 0.5f,
                (size.Y - iconSize.Y) * 0.5f), ImGui.GetColorU32(PatreonOrange), heart);
        if (!compact)
        {
            var label = PatreonTierName(tier);
            var labelSize = ImGui.CalcTextSize(label);
            draw.AddText(origin + new Vector2(Ui(27f), (size.Y - labelSize.Y) * 0.5f),
                ImGui.GetColorU32(Vector4.One), label);
        }
    }
}

using AlphaChannel.Contracts;
using Dalamud.Game.ClientState.Objects.SubKinds;

namespace AlphaChannel.Plugin;

// Club-floor / public-stream walk-up: when idle near someone whose AlphaChannel name matches their
// character name (the first-connect default), auto-join as viewer. Live pixels still need
// AlphaChannel + ScreenPainter — Lightless alone only sees a Penumbra stage prop if the host uses one.
//
// HARD-DISABLED: the scan was joining nearby names every few seconds, flipping the Player tab and
// (via older DoJoin) clearing the queue — which wiped YouTube search/URL text mid-type and felt like
// the UI was reloading. Keep the class wired so we can re-enable with HasLocalPlayback guards later.
internal sealed class NearbyAutoWatch : IDisposable
{
    private static readonly TimeSpan ScanInterval = TimeSpan.FromSeconds(2.5);
    private static readonly TimeSpan JoinGrace = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan CooldownAfterMiss = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan CooldownAfterPrivate = TimeSpan.FromSeconds(90);

    private readonly StreamClient stream;
    private readonly MainWindow mainWindow;
    private readonly Dictionary<string, DateTime> cooldowns = new(StringComparer.OrdinalIgnoreCase);

    private DateTime nextScanUtc = DateTime.MinValue;
    private string? pendingHostName;
    private DateTime pendingSinceUtc;
    private bool proximitySession;
    private bool awaitingContent;
    private bool uiOpened;

    // Flip only when deliberately re-enabling the feature after the typing/UI wipe bugs are gone.
    // Kept as a field (not const) so the parked scan body below stays compilable without CS0162.
    private static readonly bool FeatureEnabled = false;

    internal NearbyAutoWatch(StreamClient stream, MainWindow mainWindow)
    {
        this.stream = stream;
        this.mainWindow = mainWindow;
        stream.OnState += OnRemoteState;
        stream.OnDeclined += OnDeclined;
        stream.OnEnded += OnEnded;
        stream.OnJoined += OnJoined;
    }

    internal void OnFrameworkUpdate()
    {
        // FeatureEnabled is false until auto-watch is safe again (see class comment).
        if (!FeatureEnabled || !Plugin.Cfg.AutoWatchNearby || stream.AutomaticWatchPartyJoinsSuppressed)
        {
            return;
        }

        if (mainWindow.CurrentSession is null || !stream.IsConnected)
        {
            return;
        }

        // Never interrupt someone who is already playing/hosting a local screen.
        if (mainWindow.HasLocalPlayback)
        {
            return;
        }

        // Manual join/watch owns the session — don't range-leave or treat as proximity.
        if (stream.Mode == StreamMode.Viewing && !mainWindow.ProximityJoined)
        {
            ResetProximityFlags();
            return;
        }

        var now = DateTime.UtcNow;
        PruneCooldowns(now);

        if (awaitingContent && pendingHostName is not null)
        {
            if (now - pendingSinceUtc > JoinGrace)
            {
                Cooldown(pendingHostName, CooldownAfterMiss);
                LeaveProximity("no public stream content", closeUi: uiOpened);
            }

            return;
        }

        if (proximitySession && stream.Mode == StreamMode.Viewing)
        {
            if (!HostStillInRange())
            {
                LeaveProximity("host left range", closeUi: true);
            }

            return;
        }

        if (stream.Mode != StreamMode.None || pendingHostName is not null)
        {
            return;
        }

        if (now < nextScanUtc)
        {
            return;
        }

        nextScanUtc = now + ScanInterval;
        TryJoinNearestCandidate(now);
    }

    private void TryJoinNearestCandidate(DateTime now)
    {
        var local = Plugin.ObjectTable.LocalPlayer;
        if (local is null)
        {
            return;
        }

        var radius = Math.Clamp(Plugin.Cfg.AutoWatchRadiusYalms, 5f, 50f);
        string? bestName = null;
        var bestDist = float.MaxValue;

        foreach (var obj in Plugin.ObjectTable)
        {
            if (obj is not IPlayerCharacter character)
            {
                continue;
            }

            if (character.EntityId == local.EntityId)
            {
                continue;
            }

            var name = character.Name.TextValue;
            if (name.Length == 0 || IsCoolingDown(name, now))
            {
                continue;
            }

            var dist = Vector3.Distance(local.Position, character.Position);
            if (dist > radius || dist >= bestDist)
            {
                continue;
            }

            bestDist = dist;
            bestName = name;
        }

        if (bestName is null)
        {
            return;
        }

        pendingHostName = bestName;
        pendingSinceUtc = now;
        awaitingContent = true;
        proximitySession = true;
        uiOpened = false;
        // Silent join — no window until we know there's a public stream with a URL.
        mainWindow.BeginProximityJoin(bestName);
    }

    private bool HostStillInRange()
    {
        var hostName = mainWindow.JoinedHostDisplayName ?? pendingHostName;
        if (hostName is not { Length: > 0 })
        {
            return false;
        }

        var local = Plugin.ObjectTable.LocalPlayer;
        if (local is null)
        {
            return false;
        }

        var leaveRadius = Math.Clamp(Plugin.Cfg.AutoWatchRadiusYalms, 5f, 50f) + 5f;
        foreach (var obj in Plugin.ObjectTable)
        {
            if (obj is not IPlayerCharacter character)
            {
                continue;
            }

            if (!string.Equals(character.Name.TextValue, hostName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return Vector3.Distance(local.Position, character.Position) <= leaveRadius;
        }

        return false;
    }

    private void OnJoined()
    {
        if (!proximitySession)
        {
            return;
        }

        pendingSinceUtc = DateTime.UtcNow;
        awaitingContent = true;
    }

    private void OnRemoteState(StreamControl message)
    {
        if (!proximitySession)
        {
            return;
        }

        if (message.IsPrivate == true)
        {
            var name = mainWindow.JoinedHostDisplayName ?? pendingHostName;
            if (name is { Length: > 0 })
            {
                Cooldown(name, CooldownAfterPrivate);
            }

            LeaveProximity("private room", closeUi: uiOpened);
            return;
        }

        if (message.Url is not { Length: > 0 })
        {
            return;
        }

        awaitingContent = false;
        pendingHostName = null;
        if (!uiOpened)
        {
            mainWindow.ShowProximityViewer();
            uiOpened = true;
        }
    }

    private void OnDeclined(string? _)
    {
        if (!proximitySession)
        {
            return;
        }

        if (pendingHostName is { Length: > 0 } name)
        {
            Cooldown(name, CooldownAfterMiss);
        }

        ResetProximityFlags();
        mainWindow.ClearProximityJoin();
        // Never opened the UI for a silent miss — nothing to close.
    }

    private void OnEnded() => ResetProximityFlags();

    private void LeaveProximity(string reason, bool closeUi)
    {
        
        ResetProximityFlags();
        mainWindow.LeaveStream();
        mainWindow.ClearProximityJoin();
        if (closeUi)
        {
            mainWindow.CloseUi();
        }
    }

    private void ResetProximityFlags()
    {
        proximitySession = false;
        awaitingContent = false;
        pendingHostName = null;
        uiOpened = false;
    }

    private void Cooldown(string name, TimeSpan duration) =>
        cooldowns[name] = DateTime.UtcNow + duration;

    private bool IsCoolingDown(string name, DateTime now) =>
        cooldowns.TryGetValue(name, out var until) && until > now;

    private void PruneCooldowns(DateTime now)
    {
        foreach (var key in cooldowns.Keys.Where(k => cooldowns[k] <= now).ToList())
        {
            cooldowns.Remove(key);
        }
    }

    public void Dispose()
    {
        stream.OnState -= OnRemoteState;
        stream.OnDeclined -= OnDeclined;
        stream.OnEnded -= OnEnded;
        stream.OnJoined -= OnJoined;
    }
}

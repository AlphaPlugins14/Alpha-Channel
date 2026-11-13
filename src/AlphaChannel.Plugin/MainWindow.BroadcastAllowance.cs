using AlphaChannel.Plugin.BroadcastAllowance;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private bool allowanceExhaustionStopInProgress;
    private readonly Queue<double> recentBroadcastCreditRates = new();
    private BroadcastAllowanceMethod sampledBroadcastMethod;
    private DateTime broadcastUsageSampleStartedUtc;
    private DateTime nextBroadcastUsageSampleUtc;
    private double lastBroadcastUsedCredits;
    private double? recentBroadcastCreditsPerMinute;

    private string[] BroadcastViewerIds() => stream.Roster
        .Where(participant => !string.Equals(participant.UserId, stream.LocalUserId,
            StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(participant.UserId, stream.HostId, StringComparison.OrdinalIgnoreCase))
        .Select(participant => participant.UserId)
        .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    private double BroadcastViewerMultiplier()
    {
        var viewers = BroadcastViewerIds();
        var now = DateTime.UtcNow;
        var patreonViewers = viewers.Count(id => broadcastPatreonViewers.TryGetValue(id, out var expiry)
            && now < expiry);
        return BroadcastAllowanceService.ViewerConsumptionMultiplier(
            ConfiguredPatreonTier(), viewers.Length, patreonViewers);
    }

    private BroadcastAllowanceUsage BroadcastUsage(
        BroadcastAllowanceMethod method,
        double obsBitrateMbps = 0d)
    {
        var tier = ConfiguredPatreonTier();
        var quality = method switch
        {
            BroadcastAllowanceMethod.RetroGame when tier >= 2 &&
                Plugin.Cfg.GameBroadcastQuality == Video.GameBroadcastQuality.StandardNative => BroadcastAllowanceQuality.Standard,
            BroadcastAllowanceMethod.Browser when tier >= 3 &&
                Plugin.Cfg.BrowserBroadcastQuality == Video.BrowserBroadcastQuality.High => BroadcastAllowanceQuality.High,
            BroadcastAllowanceMethod.Browser when tier >= 2 &&
                Plugin.Cfg.BrowserBroadcastQuality is Video.BrowserBroadcastQuality.Standard or Video.BrowserBroadcastQuality.High => BroadcastAllowanceQuality.Standard,
            BroadcastAllowanceMethod.LocalVideo when tier >= 3 &&
                Plugin.Cfg.LocalVideoBroadcastQuality == Video.LocalVideoBroadcastQuality.High => BroadcastAllowanceQuality.High,
            BroadcastAllowanceMethod.LocalVideo when tier >= 2 &&
                Plugin.Cfg.LocalVideoBroadcastQuality is Video.LocalVideoBroadcastQuality.Standard or Video.LocalVideoBroadcastQuality.High => BroadcastAllowanceQuality.Standard,
            _ => BroadcastAllowanceQuality.DataSaver,
        };
        // External DJ software retains the 128 kbps baseline. The studio knows
        // its encoder setting, so both estimates and active charging use it.
        var audioBitrate = method == BroadcastAllowanceMethod.DjLive &&
            (studioBroadcastRequested || musicTab == MusicTab.Studio && !djBroadcastingToWatchParty)
            ? AllowedStudioBitrate(studioInitialized ? studioSettings.Bitrate : (Plugin.Cfg.StudioSettings?.Bitrate ?? 192))
            : 128;
        return new(method, obsBitrateMbps, BroadcastViewerMultiplier(), quality, audioBitrate);
    }

    private BroadcastAllowanceUsage ActiveBroadcastAllowanceUsage()
    {
        if (stream.Mode != StreamMode.Hosting)
        {
            return BroadcastAllowanceUsage.None;
        }

        // The host is not counted as a viewer by the room roster. A broadcast
        // with nobody else present consumes no local or Worker allowance.
        if (BroadcastViewerIds().Length == 0)
        {
            return BroadcastAllowanceUsage.None;
        }

        if (studioBroadcastRequested) return BroadcastUsage(BroadcastAllowanceMethod.DjLive);
        var engine = screenController.Engine;
        if (engine.IsLocalVideoBroadcasting)
        {
            return BroadcastUsage(BroadcastAllowanceMethod.LocalVideo);
        }

        if (engine.IsBrowserBroadcasting)
        {
            return BroadcastUsage(BroadcastAllowanceMethod.Browser);
        }

        if (engine.IsSnesBroadcasting ||
            engine.IsGameBoyBroadcasting ||
            engine.IsNesBroadcasting ||
            engine.IsGameBoyAdvanceBroadcasting ||
            engine.IsMasterSystemBroadcasting ||
            engine.IsGameGearBroadcasting)
        {
            return BroadcastUsage(BroadcastAllowanceMethod.RetroGame);
        }

        if (djBroadcastingToWatchParty)
        {
            return BroadcastUsage(BroadcastAllowanceMethod.DjLive);
        }

        if (queue.Current is { IsTransient: true } current &&
            string.Equals(
                current.Source,
                "Live Stream",
                StringComparison.Ordinal))
        {
            return BroadcastUsage(
                BroadcastAllowanceMethod.Obs,
                Math.Max(0d, Interlocked.CompareExchange(
                    ref obsMeasuredBitrateMbps,
                    0d,
                    0d)));
        }

        return BroadcastAllowanceUsage.None;
    }

    internal void UpdateBroadcastAllowance()
    {
        UpdateOneTimeAllowanceRepair();
        UpdateBroadcastPatreonViewers();
        var usage = ActiveBroadcastAllowanceUsage();
        var allowed = broadcastAllowance.Update(
            CurrentSession?.AccountId,
            ConfiguredPatreonTier(),
            usage);

        UpdateRecentBroadcastCreditRate(usage);

        if (usage.Method == BroadcastAllowanceMethod.None)
        {
            allowanceExhaustionStopInProgress = false;
        }
        else if ((!allowed || AccountBroadcastRestricted) && !allowanceExhaustionStopInProgress)
        {
            allowanceExhaustionStopInProgress = true;
            StopBroadcastForExhaustedAllowance(usage.Method);
        }
    }

    internal bool IsChargeableBroadcastActive() =>
        ActiveBroadcastAllowanceUsage().Method != BroadcastAllowanceMethod.None;

    private void UpdateRecentBroadcastCreditRate(
        BroadcastAllowanceUsage usage)
    {
        if (usage.Method == BroadcastAllowanceMethod.None)
        {
            ResetRecentBroadcastCreditRate();
            return;
        }

        var now = DateTime.UtcNow;
        if (sampledBroadcastMethod != usage.Method)
        {
            recentBroadcastCreditRates.Clear();
            recentBroadcastCreditsPerMinute = null;
            sampledBroadcastMethod = usage.Method;
            broadcastUsageSampleStartedUtc = now;
            nextBroadcastUsageSampleUtc = now.AddMinutes(1);
            var initial = broadcastAllowance.GetSnapshot(
                CurrentSession?.AccountId,
                ConfiguredPatreonTier(),
                usage);
            lastBroadcastUsedCredits =
                initial.TotalTokens - initial.RemainingTokens;
            return;
        }

        if (now < nextBroadcastUsageSampleUtc)
        {
            return;
        }

        var snapshot = broadcastAllowance.GetSnapshot(
            CurrentSession?.AccountId,
            ConfiguredPatreonTier(),
            usage);
        var usedCredits = snapshot.TotalTokens - snapshot.RemainingTokens;
        var elapsedMinutes = Math.Max(
            1d / 60d,
            (now - broadcastUsageSampleStartedUtc).TotalMinutes);

        if (usedCredits < lastBroadcastUsedCredits)
        {
            recentBroadcastCreditRates.Clear();
            recentBroadcastCreditsPerMinute = null;
        }
        else
        {
            recentBroadcastCreditRates.Enqueue(
                (usedCredits - lastBroadcastUsedCredits) / elapsedMinutes);
            while (recentBroadcastCreditRates.Count > 5)
            {
                recentBroadcastCreditRates.Dequeue();
            }

            recentBroadcastCreditsPerMinute =
                recentBroadcastCreditRates.Average();
        }

        lastBroadcastUsedCredits = usedCredits;
        broadcastUsageSampleStartedUtc = now;
        nextBroadcastUsageSampleUtc = now.AddMinutes(1);
    }

    private void ResetRecentBroadcastCreditRate()
    {
        sampledBroadcastMethod = BroadcastAllowanceMethod.None;
        broadcastUsageSampleStartedUtc = default;
        nextBroadcastUsageSampleUtc = default;
        lastBroadcastUsedCredits = 0d;
        recentBroadcastCreditRates.Clear();
        recentBroadcastCreditsPerMinute = null;
    }

    private BroadcastAllowanceMethod HostBroadcastPanelMethod()
    {
        var active = ActiveBroadcastAllowanceUsage();
        if (active.Method != BroadcastAllowanceMethod.None)
        {
            return active.Method;
        }

        if (localVideoBroadcastArmed)
        {
            return BroadcastAllowanceMethod.LocalVideo;
        }

        if (browserBroadcastArmed)
        {
            return BroadcastAllowanceMethod.Browser;
        }

        if (gameBroadcastArmed)
        {
            return BroadcastAllowanceMethod.RetroGame;
        }

        if (djBroadcastingToWatchParty)
        {
            return BroadcastAllowanceMethod.DjLive;
        }

        if (queue.Current is { IsTransient: true } current &&
            string.Equals(
                current.Source,
                "Live Stream",
                StringComparison.OrdinalIgnoreCase))
        {
            return BroadcastAllowanceMethod.Obs;
        }

        return BroadcastAllowanceMethod.None;
    }

    private bool CanStartBroadcastWithAllowance(
        BroadcastAllowanceMethod method)
    {
        if (AccountBroadcastRestricted) { accountAccessMethod = method; accountAccessPopup = true; return false; }
        if (broadcastAllowance.CanStart(
                CurrentSession?.AccountId,
                ConfiguredPatreonTier()))
        {
            return true;
        }

        Plugin.ChatGui.Print(
            "[AlphaChannel] Your weekly broadcast allowance has been used. It resets next Monday at 00:00 UTC.");
        return false;
    }

    private BroadcastAllowanceSnapshot BroadcastAllowanceSnapshotFor(
        BroadcastAllowanceMethod method,
        double obsBitrateMbps = 0d) =>
        broadcastAllowance.GetSnapshot(
            CurrentSession?.AccountId,
            ConfiguredPatreonTier(),
            BroadcastUsage(method, obsBitrateMbps));

    private static string FormatWholeCredits(double credits) =>
        Math.Round(
            Math.Max(0d, credits),
            MidpointRounding.AwayFromZero)
        .ToString("0");

    private AllowanceDisplay BroadcastAllowanceDisplayFor(
        BroadcastAllowanceMethod method,
        double obsBitrateMbps = 0d)
    {
        if (AccountBroadcastRestricted)
            return new(0, "BROADCAST ACCESS", "Unavailable", AccountBroadcastRequirement(method), true, true);
        var snapshot = BroadcastAllowanceSnapshotFor(
            method,
            obsBitrateMbps);
        if (!snapshot.StorageHealthy)
        {
            return new AllowanceDisplay(
                0f,
                "FREE WEEKLY BROADCAST ALLOWANCE",
                "Unavailable",
                broadcastAllowance.StorageErrorText,
                snapshot.PatreonTier == 0,
                true);
        }

        var highBandwidth =
            method == BroadcastAllowanceMethod.Obs &&
            snapshot.TokensPerMinute >= 5d;
        var detail = highBandwidth
            ? "High bandwidth · Resets Monday (00:00 UTC)"
            : "Resets Monday (00:00 UTC)";

        return new AllowanceDisplay(
            (float)snapshot.RemainingFraction,
            snapshot.PatreonTier > 0
                ? $"PATREON TIER {snapshot.PatreonTier} WEEKLY BROADCAST ALLOWANCE"
                : "FREE WEEKLY BROADCAST ALLOWANCE",
            $"{FormatWholeCredits(snapshot.RemainingTokens)} / {FormatWholeCredits(snapshot.TotalTokens)} credits",
            detail,
            snapshot.PatreonTier == 0,
            highBandwidth);
    }

    private readonly record struct AllowanceDisplay(
        float Remaining,
        string Heading,
        string RemainingLabel,
        string Detail,
        bool ShowPatreonPrompt,
        bool Warning);

    private void StopBroadcastForExhaustedAllowance(
        BroadcastAllowanceMethod method)
    {
        switch (method)
        {
            case BroadcastAllowanceMethod.DjLive:
                StopStudioBroadcast();
                djBroadcastingToWatchParty = false;
                StopPlayback();
                break;

            case BroadcastAllowanceMethod.RetroGame:
                StopGameWatchPartyBroadcast();
                break;

            case BroadcastAllowanceMethod.Browser:
                StopBrowserWatchPartyBroadcast();
                break;

            case BroadcastAllowanceMethod.LocalVideo:
                StopLocalVideoWatchPartyBroadcast();
                break;

            case BroadcastAllowanceMethod.Obs:
                StopPlayback();
                break;
        }

        Plugin.ChatGui.Print(
            "[AlphaChannel] Your weekly broadcast allowance has been used. Broadcasting has stopped.");
    }
}

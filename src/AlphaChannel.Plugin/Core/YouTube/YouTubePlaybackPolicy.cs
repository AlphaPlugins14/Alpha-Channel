namespace AlphaChannel.Plugin.Video;

internal enum YouTubePlaybackRoute
{
    Android,
    PoToken,
    AndroidProbe
}

/// <summary>
/// Chooses between fast Android extraction and PO-token compatibility
/// mode.
///
/// Normal operation uses Android. A recognised YouTube bot check enables
/// PO-token mode for one hour. Once that period expires, the next video
/// probes Android while the HTTP provider remains ready.
/// </summary>
internal sealed class YouTubePlaybackPolicy
{
    private static readonly TimeSpan CompatibilityDuration =
        TimeSpan.FromHours(1);

    private static readonly TimeSpan ProviderStopDelay =
        TimeSpan.FromSeconds(10);

    private readonly Configuration configuration;
    private readonly YouTubePoTokenService poTokenService;
    private readonly object syncRoot =
        new();

    private bool probeInProgress;
    private int stateGeneration;
    private readonly Func<bool> providerFilesAvailable;
    private readonly Func<bool> androidFallbackRequired;

    internal YouTubePlaybackPolicy(
        Configuration configuration,
        YouTubePoTokenService poTokenService,
        Func<bool>? providerFilesAvailable = null,
        Func<bool>? androidFallbackRequired = null)
    {
        this.androidFallbackRequired = androidFallbackRequired ?? (() => false);
        this.providerFilesAvailable = providerFilesAvailable ?? (() => true);
        this.configuration =
            configuration;

        this.poTokenService =
            poTokenService;

        //
        // Remove an expired value left by a previous plugin session.
        // There is no provider process to retain after a full restart.
        //
        if (configuration.YouTubePoCompatibilityUntilUnixSeconds > 0 &&
            GetCompatibilityUntilUtc() <= DateTimeOffset.UtcNow)
        {
            configuration.YouTubePoCompatibilityUntilUnixSeconds =
                0;

            configuration.Save();
        }
    }

    internal bool CompatibilityModeActive
    {
        get
        {
            lock (syncRoot)
            {
                return
                    !androidFallbackRequired() && (configuration
                        .PreferHighQualityYouTubeVideos ||
                    GetCompatibilityUntilUtc() >
                        DateTimeOffset.UtcNow);
            }
        }
    }

    /// <summary>
    /// Chooses the route for a newly requested YouTube video.
    /// This does not start or stop currently playing media.
    /// </summary>
    internal YouTubePlaybackRoute SelectRoute()
    {
        lock (syncRoot)
        {
            if (androidFallbackRequired())
                return YouTubePlaybackRoute.Android;

            //
            // The user's explicit quality preference takes priority over
            // the automatic Android/compatibility timer policy.
            //
            if (configuration
                .PreferHighQualityYouTubeVideos)
            {
                //
                // Start preparing the provider immediately. VideoEngine
                // will still await readiness before loading the video.
                //
                _ = poTokenService
                    .EnsureStartedAsync();

                

                return
                    YouTubePlaybackRoute.PoToken;
            }

            var untilUtc =
                GetCompatibilityUntilUtc();

            if (untilUtc >
                DateTimeOffset.UtcNow)
            {
                //
                // Starting is fire-and-forget here. Callers performing a
                // retry can explicitly await EnsureProviderReadyAsync().
                //
                _ = poTokenService
                    .EnsureStartedAsync();

                return
                    YouTubePlaybackRoute.PoToken;
            }

            //
            // The compatibility timer has expired, but a provider started
            // during this plugin session remains alive. Keep it available
            // while one Android playback tests whether the restriction has
            // cleared.
            //
            if (poTokenService.IsRunning)
            {
                if (!probeInProgress)
                {
                    probeInProgress =
                        true;

                    

                    return
                        YouTubePlaybackRoute.AndroidProbe;
                }

                //
                // Avoid running multiple Android probes simultaneously.
                // Other requests retain the dependable PO route until the
                // active probe produces a result.
                //
                return
                    YouTubePlaybackRoute.PoToken;
            }

            return
                YouTubePlaybackRoute.Android;
        }
    }

    /// <summary>
    /// Ensures the HTTP provider is ready before a PO-token retry.
    /// Script mode remains available if HTTP startup fails.
    /// </summary>
    internal async Task<bool> EnsureProviderReadyAsync()
    {
        var ready = await poTokenService.EnsureStartedAsync().ConfigureAwait(false);
        if (!providerFilesAvailable())
            throw new InvalidOperationException("YouTube PO-token dependencies could not be installed. Check the download connection and try again shortly.");
        return ready;
    }

    internal async Task<bool> EnsureProviderReadyAsync(TimeSpan startupTimeout, CancellationToken cancellationToken)
    {
        var ready = await poTokenService.EnsureStartedAsync(startupTimeout, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (!providerFilesAvailable())
            throw new InvalidOperationException("YouTube PO-token dependencies could not be installed. Check the download connection and try again shortly.");
        return ready;
    }

    // A terminal extractor error need not contain the exact bot-check wording.
    // Use the existing single resolver fallback to try the installed alternate
    // route, without treating a generic failure as proof of a bot restriction
    // or changing the saved quality/compatibility preference.
    internal bool ShouldUsePoTokenFallback(YouTubePlaybackRoute failedRoute, string? failure) =>
        !androidFallbackRequired() && providerFilesAvailable() &&
        (failedRoute == YouTubePlaybackRoute.PoToken || !IsAccountRequiredError(failure));

    internal Task StopProviderWhenIdleAsync()
    {
        lock (syncRoot)
        {
            probeInProgress = false;
            stateGeneration++;
        }
        return poTokenService.StopAsync();
    }

    /// <summary>
    /// Returns true only for errors that strongly indicate YouTube's
    /// automated traffic/bot verification.
    ///
    /// Generic HTTP failures are deliberately excluded because they may
    /// be ordinary CDN or network errors.
    /// </summary>
    internal static bool IsBotCheckError(
        string? message)
    {
        if (string.IsNullOrWhiteSpace(
                message))
        {
            return false;
        }

        var normalized =
            message.ToLowerInvariant();

        return
            normalized.Contains(
                "sign in to confirm you're not a bot") ||
            normalized.Contains(
                "sign in to confirm you’re not a bot") ||
            normalized.Contains(
                "confirm you are not a bot") ||
            normalized.Contains(
                "login_required") ||
            normalized.Contains(
                "this helps protect our community") ||
            normalized.Contains(
                "only images are available");
    }

    /// <summary>
    /// Identifies failures where a deliberately connected YouTube account can
    /// provide access. General bot checks, geo restrictions, unavailable
    /// videos and network failures are intentionally excluded.
    /// </summary>
    internal static bool IsAccountRequiredError(
        string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return false;

        var normalized =
            message.ToLowerInvariant();

        return normalized.Contains("sign in to confirm your age") ||
               normalized.Contains("sign in to confirm age") ||
               normalized.Contains("age-restricted") ||
               normalized.Contains("age restricted") ||
               normalized.Contains("members-only") ||
               normalized.Contains("members only") ||
               normalized.Contains("channel members") ||
               normalized.Contains("join this channel") ||
               normalized.Contains("this video is private") ||
               normalized.Contains("private video") ||
               normalized.Contains("authentication required") ||
               normalized.Contains("login_required");
    }

    /// <summary>
    /// Activates or extends PO-token compatibility mode following a
    /// recognised Android bot check.
    /// </summary>
    internal async Task EnterCompatibilityModeAsync()
    {
        if (androidFallbackRequired())
        {
            {}
            return;
        }
        DateTimeOffset untilUtc;

        lock (syncRoot)
        {
            untilUtc =
                DateTimeOffset.UtcNow +
                CompatibilityDuration;

            configuration.YouTubePoCompatibilityUntilUnixSeconds =
                untilUtc.ToUnixTimeSeconds();

            probeInProgress =
                false;

            stateGeneration++;

            configuration.Save();
        }

        

        await EnsureProviderReadyAsync()
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Called only after the selected route has reached confirmed media
    /// playback, not merely after mpv accepted a load command.
    /// </summary>
    internal void ReportPlaybackSucceeded(
        YouTubePlaybackRoute route)
    {
        if (route !=
            YouTubePlaybackRoute.AndroidProbe)
        {
            return;
        }

        int successfulGeneration;

        lock (syncRoot)
        {
            probeInProgress =
                false;

            configuration.YouTubePoCompatibilityUntilUnixSeconds =
                0;

            successfulGeneration =
                ++stateGeneration;

            configuration.Save();
        }

        

        //
        // Allow any in-flight token request to finish before stopping
        // the provider. A later bot check increments stateGeneration and
        // prevents this delayed stop.
        //
        _ = StopProviderAfterSuccessfulProbeAsync(
            successfulGeneration);
    }

    /// <summary>
    /// Clears an inconclusive probe without changing compatibility mode.
    /// This is used for failures unrelated to bot verification.
    /// </summary>
    internal void ReportProbeInconclusive()
    {
        lock (syncRoot)
        {
            probeInProgress =
                false;
        }

        
    }

    private async Task StopProviderAfterSuccessfulProbeAsync(
        int expectedGeneration)
    {
        try
        {
            await Task.Delay(
                    ProviderStopDelay)
                .ConfigureAwait(false);

            lock (syncRoot)
            {
                if (stateGeneration !=
                        expectedGeneration ||
                    GetCompatibilityUntilUtc() >
                        DateTimeOffset.UtcNow ||
                    probeInProgress)
                {
                    return;
                }
            }

            await poTokenService
                .StopAsync()
                .ConfigureAwait(false);
        }
        catch (Exception)
        {
            
        }
    }

    private DateTimeOffset GetCompatibilityUntilUtc()
    {
        var value =
            configuration
                .YouTubePoCompatibilityUntilUnixSeconds;

        if (value <= 0)
        {
            return
                DateTimeOffset.MinValue;
        }

        try
        {
            return
                DateTimeOffset.FromUnixTimeSeconds(
                    value);
        }
        catch (ArgumentOutOfRangeException)
        {
            return
                DateTimeOffset.MinValue;
        }
    }
}

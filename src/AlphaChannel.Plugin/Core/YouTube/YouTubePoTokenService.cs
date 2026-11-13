using System.Diagnostics;

namespace AlphaChannel.Plugin.Video;

/// <summary>
/// Manages the local BgUtils HTTP provider.
///
/// The provider can be started when YouTube compatibility mode is
/// activated and stopped after a successful Android probe.
///
/// Only a provider process started by this instance will be stopped.
/// An already-running external provider is reused but never killed.
/// </summary>
internal sealed class YouTubePoTokenService : IDisposable
{
    private const int Port =
        4416;

    private static readonly Uri PingUri =
        new(
            $"http://127.0.0.1:{Port}/ping");

    private readonly HttpClient httpClient;
    private readonly CancellationTokenSource shutdown;
    private readonly CancellationToken shutdownToken;
    private readonly SemaphoreSlim lifecycleLock =
        new(
            1,
            1);

    private Process? ownedProcess;
    private bool usingExternalProvider;
    private volatile bool disposed;
    private int disposeStarted;
    private readonly Func<Task<bool>> ensureInstalled;

    internal YouTubePoTokenService(Func<Task<bool>>? ensureInstalled = null)
    {
        this.ensureInstalled = ensureInstalled ?? (() => Task.FromResult(true));
        httpClient =
            new HttpClient
            {
                Timeout =
                    TimeSpan.FromSeconds(1)
            };

        shutdown =
            new CancellationTokenSource();
        shutdownToken = shutdown.Token;
    }

    internal bool IsRunning
    {
        get
        {
            if (disposed) return false;
            if (usingExternalProvider) return true;
            var process = ownedProcess;
            try { return process is { HasExited: false }; }
            catch (InvalidOperationException) { return false; }
        }
    }

    /// <summary>
    /// Compatibility wrapper for the current Resources implementation.
    /// It can be removed after Resources stops launching the service
    /// automatically.
    /// </summary>
    internal async Task StartAsync()
    {
        await EnsureStartedAsync()
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Ensures that the local provider is ready and returns true when
    /// its /ping endpoint responds successfully.
    /// </summary>
    internal Task<bool> EnsureStartedAsync() => EnsureStartedAsync(null, CancellationToken.None);

    internal async Task RefreshTokensAsync(CancellationToken cancellationToken)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(shutdownToken, cancellationToken);
        await lifecycleLock.WaitAsync(lifetime.Token).ConfigureAwait(false);
        try
        {
            lifetime.Token.ThrowIfCancellationRequested();
            // A failed HTTP launch still permits the installed script provider.
            // Its cache is isolated by the recovery extractor's environment.
            if (!IsRunning) return;
            await YouTubePoTokenRefresh.InvalidateAsync(httpClient, lifetime.Token).ConfigureAwait(false);
        }
        finally { lifecycleLock.Release(); }
    }

    // Activities can allow a cold provider to start throughout their setup
    // window instead of killing it after the ordinary short startup probe.
    internal async Task<bool> EnsureStartedAsync(TimeSpan? startupTimeout, CancellationToken cancellationToken)
    {
        if (disposed)
        {
            return false;
        }

        using var startupLifetime = CancellationTokenSource.CreateLinkedTokenSource(shutdownToken, cancellationToken);
        var startupToken = startupLifetime.Token;
        var startedProcess = false;
        try { await lifecycleLock.WaitAsync(startupToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { return false; }

        try
        {
            if (disposed)
            {
                return false;
            }

            if (!await ensureInstalled().WaitAsync(startupToken).ConfigureAwait(false) || disposed)
                return false;

            //
            // This also detects a provider started by another instance.
            //
            if (await IsProviderHealthyAsync(
                    startupToken)
                .ConfigureAwait(false))
            {
                if (ownedProcess is null)
                {
                    usingExternalProvider =
                        true;

                    
                }

                return true;
            }

            startupToken.ThrowIfCancellationRequested();
            usingExternalProvider =
                false;

            //
            // A previously owned process may have exited.
            //
            if (ownedProcess is not null)
            {
                StopOwnedProcess();
            }

            if (!TryGetRuntimePaths(
                    out var nodePath,
                    out var serverDirectory,
                    out var mainScript))
            {
                {}

                return false;
            }

            var process =
                new Process
                {
                    StartInfo =
                        new ProcessStartInfo
                        {
                            FileName =
                                nodePath,

                            WorkingDirectory =
                                serverDirectory,

                            UseShellExecute =
                                false,

                            CreateNoWindow =
                                true,

                            RedirectStandardOutput =
                                true,

                            RedirectStandardError =
                                true
                        },

                    EnableRaisingEvents =
                        true
                };

            process.StartInfo.ArgumentList.Add(
                mainScript);

            //
            // Explicitly bind the installed provider to IPv4 loopback.
            //
            process.StartInfo.ArgumentList.Add(
                "--port");

            process.StartInfo.ArgumentList.Add(
                Port.ToString(
                    System.Globalization.CultureInfo.InvariantCulture));
            process.StartInfo.ArgumentList.Add("--host");
            process.StartInfo.ArgumentList.Add("127.0.0.1");

            process.OutputDataReceived +=
                OnProviderOutput;

            process.ErrorDataReceived +=
                OnProviderError;

            process.Exited +=
                OnProviderExited;

            if (!process.Start())
            {
                process.Dispose();

                {}

                return false;
            }

            ownedProcess =
                process;
            startedProcess = true;

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            

            var startupClock = Stopwatch.StartNew();
            for (var attempt = 0;
                 startupTimeout is { } timeout ? startupClock.Elapsed < timeout : attempt < 25;
                 attempt++)
            {
                startupToken
                    .ThrowIfCancellationRequested();

                if (process.HasExited)
                {
                    {}

                    StopOwnedProcess();
                    return false;
                }

                if (await IsProviderHealthyAsync(
                        startupToken)
                    .ConfigureAwait(false))
                {
                    

                    return true;
                }

                await Task.Delay(
                        TimeSpan.FromMilliseconds(startupTimeout.HasValue ? 500 : 200),
                        startupToken)
                    .ConfigureAwait(false);
            }

            AepLog.Warning("[YouTube] PO-token provider startup timed out.");

            StopOwnedProcess();
            return false;
        }
        catch (OperationCanceledException)
        {
            if (startedProcess) StopOwnedProcess();
            return false;
        }
        catch (Exception ex)
        {
            AepLog.Warning("[YouTube] PO-token provider startup failed: " + ex.GetType().Name);

            StopOwnedProcess();
            return false;
        }
        finally
        {
            lifecycleLock.Release();
        }
    }

    /// <summary>
    /// Stops the provider if Alpha Channel started it.
    /// An externally-owned provider is left running.
    /// </summary>
    internal async Task StopAsync()
    {
        if (disposed)
        {
            return;
        }

        await lifecycleLock
            .WaitAsync()
            .ConfigureAwait(false);

        try
        {
            if (disposed) return;
            if (ownedProcess is null)
            {
                //
                // We do not own an already-running external provider.
                //
                usingExternalProvider =
                    false;

                return;
            }

            StopOwnedProcess();

            
        }
        finally
        {
            lifecycleLock.Release();
        }
    }

    private async Task<bool> IsProviderHealthyAsync(
        CancellationToken token)
    {
        try
        {
            using var response =
                await httpClient.GetAsync(
                        PingUri,
                        token)
                    .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return false;
            }

            var body =
                await response.Content
                    .ReadAsStringAsync(
                        token)
                    .ConfigureAwait(false);

            return
                body.Contains(
                    "server_uptime",
                    StringComparison.OrdinalIgnoreCase) &&
                body.Contains(
                    "version",
                    StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static bool TryGetRuntimePaths(
        out string nodePath,
        out string serverDirectory,
        out string mainScript)
    {
        var rootDirectory =
            Path.Combine(
                Plugin.PluginInterface
                    .ConfigDirectory
                    .FullName,
                "youtube-po-token");

        nodePath =
            Path.Combine(
                rootDirectory,
                "node",
                "node.exe");

        serverDirectory =
            Path.Combine(
                rootDirectory,
                "provider",
                "server");

        mainScript =
            Path.Combine(
                serverDirectory,
                "build",
                "main.js");

        return
            File.Exists(nodePath) &&
            Directory.Exists(serverDirectory) &&
            File.Exists(mainScript);
    }

    private static void OnProviderOutput(
      object sender,
      DataReceivedEventArgs args)
    {
        // Drain provider stdout without retaining tokens or diagnostic output.
    }

    private static void OnProviderError(
        object sender,
        DataReceivedEventArgs args)
    {
        // Provider stderr is drained without logging; playback reports terminal failures.
    }

    private void OnProviderExited(object? sender, EventArgs args)
    {
        // Startup and playback paths determine whether a provider exit is terminal.
    }

    private void StopOwnedProcess()
    {
        var process =
            Interlocked.Exchange(
                ref ownedProcess,
                null);

        if (process is null)
        {
            return;
        }

        try
        {
            process.Exited -=
                OnProviderExited;

            if (!process.HasExited)
            {
                process.Kill(
                    entireProcessTree: true);

                process.WaitForExit(
                    2000);
            }
        }
        catch (Exception)
        {
            
        }
        finally
        {
            process.Dispose();
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposeStarted, 1) != 0) return;
        disposed = true;
        shutdown.Cancel();
        if (lifecycleLock.Wait(TimeSpan.FromSeconds(3)))
        {
            CompleteShutdown();
            return;
        }
        // A startup/installation worker still owns the lock. Do not destroy
        // its process and client concurrently merely because the wait expired.
        {}
        _ = FinishShutdownAsync();
    }

    private async Task FinishShutdownAsync()
    {
        await lifecycleLock.WaitAsync().ConfigureAwait(false);
        CompleteShutdown();
    }

    private void CompleteShutdown()
    {
        try
        {
            StopOwnedProcess();
            shutdown.Dispose();
            httpClient.Dispose();
        }
        finally
        {
            // Queued StopAsync callers may still release the semaphore.
            // It has no native wait handle and can safely be collected.
            lifecycleLock.Release();
        }
    }
}

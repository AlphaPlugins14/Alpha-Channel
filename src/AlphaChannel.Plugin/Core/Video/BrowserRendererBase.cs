using System.Buffers;
using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using AlphaChannel.BrowserShared;
using SharpDX;
using SharpDX.Direct3D11;
using SixLabors.Fonts;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace AlphaChannel.Plugin.Video;

// Shared out-of-process browser transport, texture upload, input and broadcast.
// Each browser implementation selects its own helper and runtime directory.
internal abstract class BrowserRendererBase : IDisposable
{
    protected abstract string RuntimeFolder { get; }
    protected abstract string HostExecutable { get; }
    protected virtual bool PlayCapturedAudioLocally => true;
    internal const int Width = 960;
    internal const int Height = 540;
    private const int FramesPerSecond = 20;
    private const int AudioSampleRate = 48000;
    private readonly string renderKey = $"AlphaChannel.Browser.{Guid.NewGuid():N}";
    private readonly Texture2D target;
    private readonly BrowserFrameScaler frameScaler;
    private readonly object frameLock = new();
    private readonly object writeLock = new();
    private readonly object broadcastLifetimeLock = new();
    private readonly object audioTimelineLock = new();
    private BrowserAudioTimeline? audioTimeline;
    private int disposeStarted;
    private readonly byte[] broadcastFrame = new byte[Width * Height * 4];
    private readonly byte[] privacyFrame;
    private NamedPipeServerStream? pipe;
    private NamedPipeServerStream? audioPipe;
    private BinaryReader? reader;
    private BinaryReader? audioReader;
    private BinaryWriter? writer;
    private Process? process;
    private BrowserProcessJob? processJob;
    private CancellationTokenSource? cancellation;
    private Task? hostMessagesTask;
    private Task? hostAudioTask;
    private Snes9xAudio? localAudio;
    private GameBroadcastEncoder? encoder;
    private CancellationTokenSource? broadcastCancellation;
    private Task? broadcastTask;
    private Task broadcastCleanup = Task.CompletedTask;
    private Task hostCleanup = Task.CompletedTask;
    internal Task CleanupCompletion => hostCleanup;
    private byte[]? pendingFrame;
    private BrowserState state = new("https://www.google.com/", "Browser", true, false, false, false);
    private long lastVideoTick;
    private volatile bool disposed;
    private volatile bool privacyMode;
    private volatile bool accountConnectionPrivacy;
    private readonly object youtubeCookieRequestLock = new();
    private TaskCompletionSource<YouTubeCookieExportResult>? youtubeCookieRequest;
    private TaskCompletionSource<YouTubeAccountLookupResult>? youtubeProfileRequest;
    private string? youtubeProfileRequestId;

    internal string Address => state.Address;
    internal string Title => string.IsNullOrWhiteSpace(state.Title) ? "Browser" : state.Title;
    internal string? LastError { get; private set; }
    internal sealed record NavigationNotice(string Id, string Url, string SourceHost);
    private NavigationNotice? navigationNotice;
    internal NavigationNotice? PendingNavigation => Volatile.Read(ref navigationNotice);
    internal void ResolveNavigation(NavigationNotice notice, bool allow, bool ignoreAll = false) =>
        Send(25, JsonSerializer.SerializeToUtf8Bytes(new { notice.Id, Allow = allow, IgnoreAll = ignoreAll }));
    internal void SetRedirectWhitelist(IEnumerable<string> hosts) => Send(26, JsonSerializer.SerializeToUtf8Bytes(hosts.ToArray()));
    internal void SetAlphaChannelRedirectWhitelistEnabled(bool enabled) => SendBinary(27, writer => writer.Write(enabled));
    internal bool IsReady
    {
        get
        {
            var current = process;
            if (disposed || current is null || !state.IsReady) return false;
            try { return !current.HasExited; }
            catch (InvalidOperationException) { return false; }
        }
    }
    internal bool IsLoading => state.IsLoading;
    internal bool CanGoBack => state.CanGoBack;
    internal bool CanGoForward => state.CanGoForward;
    internal bool IsBroadcasting => encoder?.IsRunning == true;
    internal bool HasFailed
    {
        get
        {
            if (disposed) return false;
            try
            {
                return process is null || process.HasExited || pipe?.IsConnected != true;
            }
            catch
            {
                return true;
            }
        }
    }
    internal string FailureMessage =>
        LastError ?? "The browser process stopped unexpectedly.";
    internal BroadcastDiagnosticsSnapshot BroadcastDiagnostics =>
        encoder?.Diagnostics ?? BroadcastDiagnosticsSnapshot.Idle;

    protected BrowserRendererBase(Texture2D target, string cacheDirectory, string? runtimeDirectory = null)
    {
        this.target = target;
        frameScaler = new BrowserFrameScaler(target, Width, Height);
        try
        {
            privacyFrame = CreatePrivacyFrame();
            localAudio = new Snes9xAudio(AudioSampleRate);
            StartHost(cacheDirectory, runtimeDirectory);
        }
        catch
        {
            localAudio?.Dispose();
            frameScaler.Dispose();
            throw;
        }
    }

    private void StartHost(string cacheDirectory, string? installedRuntimeDirectory)
    {
        try
        {
            Directory.CreateDirectory(cacheDirectory);
            var pluginDirectory = Plugin.PluginInterface.AssemblyLocation.DirectoryName
                ?? throw new InvalidOperationException("The Alpha Channel plugin directory could not be located.");
            var runtimeDirectory = installedRuntimeDirectory ?? Path.Combine(pluginDirectory, "Browsers", RuntimeFolder);
            if (RuntimeFolder == "WebView")
            {
                helperRuntime = new HelperRuntimeLease(runtimeDirectory);
                runtimeDirectory = helperRuntime.DirectoryPath;
            }
            var executable = Path.Combine(runtimeDirectory, HostExecutable);
            if (!File.Exists(executable)) throw new FileNotFoundException("The Alpha Channel browser host is missing.", executable);
            var pipeName = $"AlphaChannelBrowser_{Guid.NewGuid():N}";
            var audioPipeName = $"AlphaChannelBrowserAudio_{Guid.NewGuid():N}";
            pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous, 1024 * 1024, 1024 * 1024);
            audioPipe = new NamedPipeServerStream(audioPipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous, 64 * 1024, 64 * 1024);
            var startInfo = new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = runtimeDirectory
            };
            startInfo.ArgumentList.Add(pipeName);
            startInfo.ArgumentList.Add(cacheDirectory);
            startInfo.ArgumentList.Add(audioPipeName);
            startInfo.ArgumentList.Add(Plugin.Cfg.BrowserAdBlockEnabled ? "true" : "false");
            processJob = new BrowserProcessJob();
            process = Process.Start(startInfo) ?? throw new InvalidOperationException("The browser host could not be started.");
            processJob.Attach(process);
            
            var connection = pipe.WaitForConnectionAsync();
            var audioConnection = audioPipe.WaitForConnectionAsync();
            if (!Task.WaitAll([connection, audioConnection], TimeSpan.FromSeconds(12)))
            {
                var detail = process.HasExited ? $" It exited with code {process.ExitCode}." : string.Empty;
                throw new TimeoutException("The browser host did not connect in time." + detail);
            }
            reader = new BinaryReader(pipe, Encoding.UTF8, true);
            audioReader = new BinaryReader(audioPipe, Encoding.UTF8, true);
            writer = new BinaryWriter(pipe, Encoding.UTF8, true);
            SetRedirectWhitelist(Plugin.Cfg.BrowserRedirectWhitelist);
            SetAlphaChannelRedirectWhitelistEnabled(Plugin.Cfg.UseAlphaChannelRedirectWhitelist);
            cancellation = new CancellationTokenSource();
            var token = cancellation.Token;
            hostMessagesTask =
                Task.Run(() => ReadHostMessages(token));
            hostAudioTask =
                Task.Run(() => ReadHostAudio(token));
        }
        catch
        {
            pipe?.Dispose();
            audioPipe?.Dispose();
            try { if (process is { HasExited: false }) process.Kill(true); } catch { }
            try { processJob?.StopAndWait(); } catch { }
            processJob?.Dispose();
            processJob = null;
            process?.Dispose();
            process = null;
            helperRuntime?.Dispose(); helperRuntime = null;
            throw;
        }
    }

    private void ReadHostAudio(CancellationToken token)
    {
        var reportedProtocol = false;
        try
        {
            while (!token.IsCancellationRequested && audioPipe?.IsConnected == true)
            {
                var length = audioReader!.ReadInt32();
                var timestamped = length < 0;
                long? captureTime = null;
                if (length < 0 && length != int.MinValue)
                {
                    length = -length;
                }
                if (length <= 0 || length > AudioSampleRate * 4) throw new InvalidDataException("Invalid browser audio packet.");
                if (timestamped) captureTime = audioReader.ReadInt64();
                if (!reportedProtocol)
                {
                    
                    reportedProtocol = true;
                }
                var payload = audioReader.ReadBytes(length);
                if (payload.Length != length) throw new EndOfStreamException();
                SubmitAudio(payload, captureTime);
            }
        }
        catch (Exception exception)
        {
            if (!disposed)
                LastError = exception is IOException or EndOfStreamException or ObjectDisposedException
                    ? "The browser audio connection closed unexpectedly."
                    : $"The browser audio stream failed: {exception.Message}";
        }
    }

    private void ReadHostMessages(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested && pipe?.IsConnected == true)
            {
                var type = reader!.ReadByte();
                var length = reader.ReadInt32();
                if (length < 0 || length > Width * Height * 4 + 1024 * 1024) throw new InvalidDataException("Invalid browser-host message.");
                var payload = reader.ReadBytes(length);
                if (payload.Length != length) throw new EndOfStreamException();
                switch (type)
                {
                    case 1 when length == Width * Height * 4:
                        lock (frameLock)
                        {
                            System.Buffer.BlockCopy(payload, 0, broadcastFrame, 0, length);
                            pendingFrame = payload;
                        }
                        break;
                    case 3: state = JsonSerializer.Deserialize<BrowserState>(payload) ?? state; break;
                    case 4: LastError = Encoding.UTF8.GetString(payload); break;
                    case 8: Volatile.Write(ref navigationNotice, JsonSerializer.Deserialize<NavigationNotice>(payload)); break;
                    case 6:  break;
                    case 7:
                        if (LastError?.StartsWith("WebView2 frame capture failed:", StringComparison.Ordinal) == true)
                            LastError = null;
                        break;
                    case 9:
                        var profileResult = JsonSerializer.Deserialize<YouTubeAccountLookupResult>(payload);
                        lock (youtubeCookieRequestLock)
                        {
                            if (profileResult?.RequestId == youtubeProfileRequestId)
                                youtubeProfileRequest?.TrySetResult(profileResult!);
                        }
                        break;
                    case 5:
                        var result =
                            JsonSerializer.Deserialize<YouTubeCookieExportResult>(payload);
                        if (result is not null)
                        {
                            lock (youtubeCookieRequestLock)
                                youtubeCookieRequest?.TrySetResult(result);
                        }
                        break;
                }
            }
        }
        catch (Exception exception)
        {
            if (!disposed)
                LastError = exception is IOException or EndOfStreamException or ObjectDisposedException
                    ? "The browser host connection closed unexpectedly."
                    : $"The browser host sent invalid data: {exception.Message}";
        }
    }

    private static long AudioClockNow() => (long)(Stopwatch.GetTimestamp() * (10000000d / Stopwatch.Frequency));

    // Called with audioTimelineLock held. Codec probing and hardware fallback
    // run asynchronously, so never use the encoder's uninitialized time origin.
    private void SynchronizeAudioTimeline(GameBroadcastEncoder? activeEncoder)
    {
        var startTick = activeEncoder?.VideoTimelineStartTick ?? 0;
        audioTimeline = BrowserAudioTimeline.ForEncoderStart(audioTimeline,
            (long)(startTick * (10000000d / Stopwatch.Frequency)));
    }

    private unsafe void SubmitAudio(byte[] pcm, long? captureTime)
    {
        if (disposed) return;
        if (pcm.Length == 0 || pcm.Length % 4 != 0) return;
        fixed (byte* data = pcm)
        {
            if (PlayCapturedAudioLocally) localAudio?.Submit((IntPtr)data, pcm.Length / 4);
        }
        lock (audioTimelineLock)
        {
            SynchronizeAudioTimeline(encoder);
            if (!privacyMode && !accountConnectionPrivacy && audioTimeline is not null)
            {
                // Old installed helpers still send untimestamped PCM. Keep
                // accepting it, but new helpers preserve the source position.
                audioTimeline.Capture(pcm, captureTime ?? AudioClockNow() - pcm.Length / 4 * 10000000L / AudioSampleRate);
            }
        }
    }

    internal void OnFrameworkUpdate()
    {
        byte[]? frame;
        lock (frameLock) { frame = pendingFrame; pendingFrame = null; }
        if (frame is not null)
        {
            // The immediate context belongs to FFXIV and may only be touched from
            // the game's Present thread. Browser pages can produce frames almost
            // continuously, so uploading here on Dalamud's framework thread races
            // the NVIDIA driver and can terminate the game.
            DxHandler.RunOnRenderThread(renderKey, () => UploadFrame(frame));
        }
    }

    private unsafe void UploadFrame(byte[] frame)
    {
        if (disposed) return;
        frameScaler.Blit(frame);
    }

    private unsafe void BroadcastPump(GameBroadcastEncoder activeEncoder, CancellationToken token, double frameRate)
    {
        var frameInterval = (long)(Stopwatch.Frequency / frameRate);
        var nextVideoTick = Stopwatch.GetTimestamp();
        var nextAudioLog = nextVideoTick + Stopwatch.Frequency * 10;
        while (!token.IsCancellationRequested && activeEncoder.IsRunning)
        {
            var now = Stopwatch.GetTimestamp();
            if (now >= nextVideoTick)
            {
                lock (frameLock)
                {
                    var selectedFrame = privacyMode || accountConnectionPrivacy ? privacyFrame : broadcastFrame;
                    fixed (byte* data = selectedFrame)
                        activeEncoder.SubmitBgraVideoFrame((IntPtr)data, Width, Height, Width * 4, now);
                }
                lastVideoTick = now;
                // Capture only the latest image after a scheduler stall. The
                // encoder restores missing time slots on its writer thread.
                nextVideoTick = now + frameInterval;
            }

            lock (audioTimelineLock)
            {
                if (ReferenceEquals(encoder, activeEncoder)) SynchronizeAudioTimeline(activeEncoder);
                if (ReferenceEquals(encoder, activeEncoder) && audioTimeline is { } timeline)
                {
                    var audio = timeline.Render(AudioClockNow());
                    if (audio is not null)
                        fixed (byte* data = audio) activeEncoder.SubmitAudio((IntPtr)data, audio.Length / 4);
                    if (now >= nextAudioLog)
                    {
                        
                        nextAudioLog = now + Stopwatch.Frequency * 10;
                    }
                }
            }

            var remainingTicks = nextVideoTick - Stopwatch.GetTimestamp();
            if (remainingTicks > Stopwatch.Frequency / 500)
                // Audio needs its own cadence, including at 15 FPS. Sleeping a
                // full video interval would underfeed the 50 ms audio batches.
                Thread.Sleep((int)Math.Min(10, Math.Max(1, remainingTicks * 1000 / Stopwatch.Frequency - 1)));
            else
                // No useful audio work remains below the 10 ms batch size;
                // spinning here burns CPU while the encoder may need it.
                Thread.Sleep(1);
        }
    }

    internal void Navigate(string address) { LastError = null; Send(10, Encoding.UTF8.GetBytes(address)); }
    internal void Back() => Send(11);
    internal void Forward() => Send(12);
    internal void Reload() => Send(13);
    internal void StopLoading() => Send(14);
    internal virtual void SetVolume(int value)
    {
        if (!disposed) localAudio?.SetVolume(Math.Clamp(value, 0, 200));
    }
    internal void SetAdBlockEnabled(bool enabled) => SendBinary(24, writer => writer.Write(enabled));

    internal void SetPrivacyMode(bool enabled)
    {
        lock (audioTimelineLock)
        {
            privacyMode = enabled;
            if (enabled) { audioTimeline?.ClearCapturedAudio(); encoder?.DiscardPendingAudio(); encoder?.MaskPendingBrowserFrames(privacyFrame); }
        }
    }
    internal void SetAccountConnectionPrivacy(bool enabled)
    {
        lock (audioTimelineLock)
        {
            accountConnectionPrivacy = enabled;
            if (enabled) { audioTimeline?.ClearCapturedAudio(); encoder?.DiscardPendingAudio(); encoder?.MaskPendingBrowserFrames(privacyFrame); }
        }
    }

    internal async Task<YouTubeAccountLookupResult> RequestYouTubeProfileAsync(CancellationToken token)
    {
        var requestId = Guid.NewGuid().ToString("N");
        var request = new TaskCompletionSource<YouTubeAccountLookupResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        lock (youtubeCookieRequestLock)
        {
            if (youtubeProfileRequest is not null || disposed)
                throw new InvalidOperationException("The browser is unavailable for an account lookup.");
            youtubeProfileRequest = request;
            youtubeProfileRequestId = requestId;
        }
        try
        {
            Send(28, Encoding.UTF8.GetBytes(requestId));
            return await request.Task.WaitAsync(TimeSpan.FromSeconds(40), token).ConfigureAwait(false);
        }
        finally
        {
            lock (youtubeCookieRequestLock)
            {
                if (ReferenceEquals(youtubeProfileRequest, request))
                {
                    youtubeProfileRequest = null;
                    youtubeProfileRequestId = null;
                }
            }
        }
    }

    internal DateTime LastInputUtc { get; private set; }

    internal void SendMouseMove(int x, int y, bool leave = false) => SendBinary(15, w => { w.Write(x); w.Write(y); });
    internal void SendMouseClick(int x, int y, int button, bool mouseUp) { if (!mouseUp) LastInputUtc = DateTime.UtcNow; SendBinary(16, w => { w.Write(x); w.Write(y); w.Write(button); w.Write(mouseUp); }); }
    internal void SendMouseWheel(int x, int y, int dx, int dy) { if (dx != 0 || dy != 0) LastInputUtc = DateTime.UtcNow; SendBinary(17, w => { w.Write(x); w.Write(y); w.Write(dx); w.Write(dy); }); }
    internal void SendKey(int code, bool up, int modifiers) { if (!up) LastInputUtc = DateTime.UtcNow; SendBinary(18, w => { w.Write(code); w.Write(up); w.Write(modifiers); }); }
    internal void SendCharacter(char value, int modifiers) { LastInputUtc = DateTime.UtcNow; SendBinary(19, w => { w.Write(value); w.Write(modifiers); }); }
    internal void Focus(bool focused) => SendBinary(20, w => w.Write(focused));

    internal async Task<YouTubeCookieExportResult> RequestYouTubeCookiesAsync(
        CancellationToken token = default)
    {
        TaskCompletionSource<YouTubeCookieExportResult> request;

        lock (youtubeCookieRequestLock)
        {
            if (youtubeCookieRequest is not null)
            {
                return new YouTubeCookieExportResult(
                    false,
                    null,
                    "A YouTube session check is already in progress.");
            }

            request =
                new TaskCompletionSource<YouTubeCookieExportResult>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
            youtubeCookieRequest =
                request;
        }

        try
        {
            Send(22);
            return await request.Task
                .WaitAsync(
                    TimeSpan.FromSeconds(20),
                    token)
                .ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return new YouTubeCookieExportResult(
                false,
                null,
                "The browser took too long to return the YouTube session. Try again.");
        }
        finally
        {
            lock (youtubeCookieRequestLock)
            {
                if (ReferenceEquals(youtubeCookieRequest, request))
                    youtubeCookieRequest = null;
            }
        }
    }

    protected void SendBinary(byte type, Action<BinaryWriter> write)
    {
        using var memory = new MemoryStream();
        using (var payloadWriter = new BinaryWriter(memory, Encoding.UTF8, true)) write(payloadWriter);
        Send(type, memory.ToArray());
    }
    private void Send(byte type, byte[]? payload = null)
    {
        payload ??= [];
        lock (writeLock)
        {
            if (disposed || writer is null) return;
            try { writer.Write(type); writer.Write(payload.Length); writer.Write(payload); writer.Flush(); }
            catch (Exception exception) when (exception is IOException or InvalidOperationException or ObjectDisposedException)
            {
                if (!disposed) LastError = "The browser host connection closed unexpectedly.";
            }
        }
    }

    internal bool StartBroadcast(
        string ffmpegPath,
        string publishUrl,
        BrowserBroadcastQuality quality, int frameRate)
    {
        lock (broadcastLifetimeLock)
        {
        if (Volatile.Read(ref disposeStarted) != 0) return false;
        if (IsBroadcasting) return true;
        if (encoder is not null || broadcastTask is not null) StopBroadcast();
        var settings = quality.Settings(frameRate);
        var previousCleanup = broadcastCleanup;
        var next = new GameBroadcastEncoder(previousCleanup);
        if (!next.Start(ffmpegPath, publishUrl, Width, Height, settings.OutputFrameRate, AudioSampleRate,
                outputWidth: settings.OutputWidth,
                outputHeight: settings.OutputHeight,
                // Timestamped audio gets 250 ms of delivery headroom. Both
                // the timeline and encoder bound queued PCM to one second.
                maxPendingAudioPackets: 64,
                ffmpegAudioQueuePackets: 32,
                sourceName: "Web browser",
                outputFrameRate: settings.OutputFrameRate,
                videoBitrateKbps: settings.VideoBitrateKbps,
                audioBitrateKbps: settings.AudioBitrateKbps,
                inputIsBgra: true))
        { next.Dispose(); LastError = "FFmpeg failed to start the browser broadcast."; return false; }
        SendBinary(29, writer => writer.Write((int)settings.OutputFrameRate));
        lock (audioTimelineLock)
        {
            audioTimeline = null;
            encoder = next;
        }
        lastVideoTick = 0;
        var pumpCancellation = new CancellationTokenSource();
        var pumpToken = pumpCancellation.Token;
        broadcastCancellation = pumpCancellation;
        broadcastTask = Task.Run(async () =>
        {
            try
            {
                await previousCleanup.ConfigureAwait(false);
                pumpToken.ThrowIfCancellationRequested();
                BroadcastPump(next, pumpToken, settings.OutputFrameRate);
            }
            catch (OperationCanceledException) { }
            catch (Exception)
            {
                if (!disposed) AepLog.Warning("[BROWSER] Broadcast pump failed.");
            }
        });
        return true;
        }
    }
    internal void StopBroadcast()
    {
        lock (broadcastLifetimeLock)
        {
        var cancellationToStop = broadcastCancellation;
        var taskToStop = broadcastTask;
        GameBroadcastEncoder? encoderToStop;
        broadcastCancellation = null;
        broadcastTask = null;
        lock (audioTimelineLock)
        {
            encoderToStop = encoder;
            encoder = null;
            audioTimeline = null;
        }
        try { cancellationToStop?.Cancel(); } catch { }
        encoderToStop?.Dispose();
        var previousCleanup = broadcastCleanup;
        broadcastCleanup = Task.Run(async () =>
        {
            using var timing = new MediaLifecycleTiming(MediaLifecycleStage.BrowserBroadcastCleanup);
            try
            {
                await previousCleanup.ConfigureAwait(false);
                if (taskToStop is not null) await taskToStop.ConfigureAwait(false);
                if (encoderToStop is not null) await encoderToStop.CleanupCompletion.ConfigureAwait(false);
            }
            catch (Exception) { AepLog.Warning("[BROWSER] Broadcast cleanup failed."); }
            finally { cancellationToStop?.Dispose(); }
        });
        SendBinary(29, writer => writer.Write(FramesPerSecond));
        }
    }

    private static byte[] CreatePrivacyFrame()
    {
        using var image = new Image<Bgra32>(Width, Height, new Bgra32(5, 7, 14, 255));
        try
        {
            var fontPath = Path.Combine(Plugin.PluginInterface.AssemblyLocation.DirectoryName ?? string.Empty,
                "Fonts", "Inter-SemiBold.ttf");
            var collection = new FontCollection();
            var font = collection.Add(fontPath).CreateFont(34f);
            const string message = "Privacy Mode Enabled";
            var size = TextMeasurer.MeasureSize(message, new TextOptions(font));
            image.Mutate(context => context.DrawText(message, font, SixLabors.ImageSharp.Color.White,
                new PointF((Width - size.Width) / 2f, (Height - size.Height) / 2f)));
        }
        catch { }
        var pixels = new byte[Width * Height * 4];
        image.CopyPixelDataTo(pixels);
        return pixels;
    }

    private HelperRuntimeLease? helperRuntime;
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposeStarted, 1) != 0) return;
        disposed = true;
        // Silence queued local PCM immediately; release its voice only after
        // old pipe readers can no longer submit to it.
        try { localAudio?.SetVolume(0); } catch { }
        try { StopBroadcast(); } catch (Exception) { AepLog.Warning("[BROWSER] Broadcast cleanup."); }
        try { DxHandler.CancelRenderThreadWork(renderKey); } catch (Exception) { AepLog.Warning("[BROWSER] Render cleanup."); }
        lock (youtubeCookieRequestLock)
        {
            youtubeCookieRequest?.TrySetCanceled();
            youtubeCookieRequest = null;
            youtubeProfileRequest?.TrySetCanceled();
            youtubeProfileRequest = null;
            youtubeProfileRequestId = null;
        }
        cancellation?.Cancel();
        try { pipe?.Dispose(); } catch { }
        try { audioPipe?.Dispose(); } catch { }

        // Keep graphics teardown on its existing owner thread. Pipe readers,
        // helper processes and local audio retain this instance until they stop.
        try { frameScaler.Dispose(); } catch { }
        lock (frameLock) pendingFrame = null;
        hostCleanup = Task.Run(CleanupHostAsync);
        _ = hostCleanup.ContinueWith(failed =>
        {
            _ = failed.Exception;
            AepLog.Warning("[BROWSER] Background helper cleanup failed.");
        }, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
    }

    private async Task CleanupHostAsync()
    {
        using var timing = new MediaLifecycleTiming(MediaLifecycleStage.BrowserHelperCleanup);
        try { await broadcastCleanup.ConfigureAwait(false); } catch { }
        var readers = Task.WhenAll(new[] { hostMessagesTask, hostAudioTask }
            .Where(task => task is not null).Cast<Task>());

        try
        {
            await readers.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            AepLog.Warning("[BROWSER] Host pipe readers did not stop within 2 seconds.");
        }
        catch (Exception) { }

        hostMessagesTask = null;
        hostAudioTask = null;
        try
        {
            if (process is { HasExited: false } &&
                !process.WaitForExit(1500))
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(1500);
            }
        }
        catch (Exception) { AepLog.Warning("[BROWSER] Helper exit cleanup."); }
        // The root may already be gone while its render/GPU/utility children
        // are still alive. A job owns those children independently of the root.
        try
        {
            var remaining = processJob?.ProcessIds() ?? [];
            
            processJob?.StopAndWait();
            
        }
        catch (Exception) { AepLog.Warning("[BROWSER] Could not inspect shutdown."); }
        finally { processJob?.Dispose(); processJob = null; }
        try { process?.Dispose(); } catch { }
        helperRuntime?.Dispose(); helperRuntime = null;
        // Never destroy an audio voice while an old reader can still submit to
        // it, even if a reader exceeded its warning deadline during shutdown.
        try { await readers.ConfigureAwait(false); } catch { }
        try { reader?.Dispose(); } catch { }
        try { audioReader?.Dispose(); } catch { }
        try { writer?.Dispose(); } catch { }
        try { cancellation?.Dispose(); } catch { }
        try { localAudio?.Dispose(); } catch { }
    }

    private sealed record BrowserState(string Address, string Title, bool IsLoading, bool CanGoBack, bool CanGoForward, bool IsReady);
}

internal sealed record YouTubeCookieExportResult(
    bool Success,
    string? Cookies,
    string? Error);

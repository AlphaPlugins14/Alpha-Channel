using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using NAudio.Wave;
using NAudio.CoreAudioApi;
using AlphaChannel.BrowserShared;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Diagnostics;

namespace AlphaChannel.WebViewHost;

internal sealed class WebViewWindow : Form
{
    private const int CaptureWidth = 960;
    private const int CaptureHeight = 540;
    private int captureFramesPerSecond = 20;
    private readonly WebView2 view = new() { Dock = DockStyle.Fill };
    private readonly WebViewTransport transport;
    private readonly string cache;
    private readonly EasyListBlocker adBlock;
    private readonly NavigationGuard navigationGuard;
    private readonly CancellationTokenSource captureLoopCancellation = new();
    private WasapiRecorder? recorder;
    private bool closing, loading;
    private bool captureReady;
    private bool hasLoadedDocument;
    private DateTime nextCaptureAttempt;
    private readonly CaptureHealth captureHealth = new();
    private ulong currentNavigationId;
    private int volume = 100, tick;
    private readonly ConcurrentQueue<(byte Type, byte[] Payload)> commands = new();
    private int commandDrainScheduled;
    private int mouseButtons;
    private int mouseX = -1, mouseY = -1;
    private double inputViewportWidth = CaptureWidth, inputViewportHeight = CaptureHeight;
    private long inputViewportUpdatedAt;
    private bool updatingVolume;
    private readonly SemaphoreSlim filterWorkers = new(2);
    private Task<long>? pendingJpegDecode;
    private CoreWebView2DevToolsProtocolEventReceiver? screencastReceiver;
    private readonly object screencastLock = new();
    private string? pendingScreencastFrame;
    private bool screencastWorkerRunning, screencastActive;
    private long screencastStarted;
    private int screencastLifetimeReceived;
    internal int ExitCode { get; private set; }
    protected override bool ShowWithoutActivation => true;
    protected override CreateParams CreateParams
    {
        get { var p = base.CreateParams; p.ExStyle |= 0x08000000 | 0x80; return p; }
    }
    internal WebViewWindow(WebViewTransport transport, string cache, bool adBlockEnabled)
    {
        this.transport = transport; this.cache = cache;
        adBlock = new EasyListBlocker(_ => { }) { Enabled = adBlockEnabled };
        navigationGuard = new NavigationGuard(() => adBlock.Enabled, notice => transport.Send(8, notice));
        FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual; Location = new Point(-10000, -10000);
        ClientSize = new Size(CaptureWidth, CaptureHeight); Controls.Add(view);
        Shown += async (_, _) => await InitializeAsync();
        FormClosing += (_, _) =>
        {
            closing = true; captureLoopCancellation.Cancel(); recorder?.StopRecording();
            if (screencastReceiver is not null)
                screencastReceiver.DevToolsProtocolEventReceived -= OnScreencastFrame;
            view.CoreWebView2?.Stop();
        };
    }
    private async Task InitializeAsync()
    {
        try
        {
            var filtersReady = Task.Run(() => adBlock.InitializeAsync(cache, TimeSpan.FromSeconds(20)));
            var options = new CoreWebView2EnvironmentOptions("--autoplay-policy=no-user-gesture-required --disable-logging --disable-breakpad");
            var environment = await CoreWebView2Environment.CreateAsync(null, cache, options);
            await view.EnsureCoreWebView2Async(environment);

            await filtersReady;
            var core = view.CoreWebView2;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.AreBrowserAcceleratorKeysEnabled = false;

            core.NavigationStarting += (_, e) =>
            {
                if (navigationGuard.Cancel(e.Uri, core.Source, redirect: e.IsRedirected))
                { e.Cancel = true; captureReady = hasLoadedDocument; return; }
                currentNavigationId = e.NavigationId; loading = true; captureReady = false; SendState();
                mouseX = mouseY = -1;
                inputViewportUpdatedAt = 0;
            };
            core.DOMContentLoaded += async (_, e) =>
            {
                var navigationId = e.NavigationId;
                if (navigationId == currentNavigationId)
                { captureReady = true; hasLoadedDocument = true; navigationGuard.CompleteNavigation(core.Source); }
                try
                {
                    var source = core.Source;
                    var script = await Task.Run(() => adBlock.CosmeticScript(source));
                    if (!closing && navigationId == currentNavigationId) await core.ExecuteScriptAsync(script);
                }
                catch (Exception) { /* Navigation or shutdown can destroy the document. */ }
            };
            core.FrameCreated += (_, e) =>
            {
                // Event arguments wrap a callback-scoped COM object. Retain the
                // actual frame while this callback is active, never the event args.
                var frame = e.Frame;
                frame.DOMContentLoaded += async (_, _) =>
                    await FrameAdBlock.ApplyAsync(frame, () => closing, adBlock.CosmeticScript);
            };
            core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
            core.WebResourceRequested += async (_, e) =>
            {
                if (!adBlock.Enabled || closing) return;
                var deferral = e.GetDeferral();
                try
                {
                    if (e.ResourceContext == CoreWebView2WebResourceContext.Document &&
                        e.Request.Headers.Contains("Sec-Fetch-Dest") && e.Request.Headers.GetHeader("Sec-Fetch-Dest") == "document" &&
                        navigationGuard.Cancel(e.Request.Uri, core.Source, redirect: true))
                    {
                        e.Response = environment.CreateWebResourceResponse(new MemoryStream(), 403, "Navigation blocked", "Content-Type: text/plain");
                        return;
                    }
                    var type = e.ResourceContext.ToString().ToLowerInvariant() switch
                    {
                        "document" => "document", "xmlhttprequest" or "fetch" => "xmlhttprequest",
                        "script" => "script", "image" => "image", "stylesheet" => "stylesheet", "font" => "font",
                        "media" => "media", "ping" => "ping", "websocket" => "websocket", _ => "other"
                    };
                    var address = e.Request.Uri;
                    var source = e.Request.Headers.Contains("Referer") ? e.Request.Headers.GetHeader("Referer") : core.Source;
                    await filterWorkers.WaitAsync();
                    bool blocked;
                    try { blocked = await Task.Run(() => adBlock.Blocks(address, source, type)); }
                    finally { filterWorkers.Release(); }
                    if (!closing && blocked) e.Response = environment.CreateWebResourceResponse(new MemoryStream(), 403, "Blocked by EasyList", "Content-Type: text/plain");
                    }
                catch (Exception) { /* Browser closed or navigation changed. */ }
                finally { try { deferral.Complete(); deferral.Dispose(); } catch (Exception) { /* Browser closed. */ } }
            };
            core.NavigationCompleted += (_, e) =>
            {
                if (e.NavigationId != currentNavigationId) return;
                navigationGuard.CompleteNavigation(core.Source);
                loading = false; SendState();
                if (!e.IsSuccess && e.WebErrorStatus is not (CoreWebView2WebErrorStatus.OperationCanceled or CoreWebView2WebErrorStatus.ConnectionAborted))
                    transport.Error("WebView2 navigation failed: " + e.WebErrorStatus);
            };
            core.SourceChanged += (_, _) => SendState();
            core.DocumentTitleChanged += (_, _) => SendState();
            core.HistoryChanged += (_, _) => SendState();
            core.NewWindowRequested += (_, e) => { e.Handled = true; if (!navigationGuard.Cancel(e.Uri, core.Source, popup: true) && !adBlock.Blocks(e.Uri, core.Source, "popup")) core.Navigate(e.Uri); };
            core.ProcessFailed += (_, e) => transport.Error("WebView2 process failed: " + e.ProcessFailedKind);
            // Start commands and page loading before optional broadcast audio
            // setup. The browser already plays its local audio through Windows.
            _ = Task.Run(() => transport.ReadCommandsAsync(QueueCommand));
            await StartScreencastAsync();
            SendState(); _ = RunCaptureLoopAsync();
            _ = StartAudioAsync();

        }
        catch (Exception e) { transport.Error("WebView2 could not start: " + e.Message); ExitCode = 3; Close(); }
    }
    private async Task StartAudioAsync()
    {
        // WebView2 plays audio in its browser/renderer process tree, not in
        // this WinForms helper process. Capture that tree, never system audio.
        try
        {
            if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
            {
                var browserProcessId = view.CoreWebView2?.BrowserProcessId ?? 0;
                if (browserProcessId == 0)
                    throw new InvalidOperationException("WebView2 browser process is unavailable.");
                var nextRecorder = await Task.Run(async () => await new WasapiRecorderBuilder()
                    .WithProcessLoopback(browserProcessId)
                    .WithFormat(new WaveFormat(48000, 16, 2)).BuildAsync());
                if (closing) { nextRecorder.Dispose(); return; }
                recorder = nextRecorder;
                recorder.DataAvailable += (buffer, flags, _, qpcPosition) =>
                {
                    var captureTime = (flags & AudioClientBufferFlags.TimestampError) == 0 && qpcPosition > 0
                        ? (long)qpcPosition
                        : (long)(Stopwatch.GetTimestamp() * (10000000d / Stopwatch.Frequency))
                          - buffer.Length / 4 * 10000000L / 48000;
                    if ((flags & AudioClientBufferFlags.Silent) != 0) transport.Audio(new byte[buffer.Length], captureTime);
                    else transport.Audio(buffer, captureTime);
                };
                recorder.RecordingStopped += (_, error) =>
                {
                    if (!closing)
                        transport.Error("WebView2 audio capture stopped: " +
                            (error.Exception?.Message ?? "The recorder stopped unexpectedly."));
                };
                recorder.StartRecording();

            }
            else transport.Error("Sharing WebView2 audio requires Windows 10 version 2004 or newer.");
        }
        catch (Exception e) { if (!closing) transport.Error("WebView2 audio sharing could not start: " + e.Message); }
    }
    private void SendState()
    {
        var core = view.CoreWebView2;
        transport.State(core.Source, core.DocumentTitle, loading, core.CanGoBack, core.CanGoForward, true);
    }
    private async Task RunCaptureLoopAsync()
    {
        var token = captureLoopCancellation.Token;
        try
        {
            while (!closing && !token.IsCancellationRequested)
            {
                var started = Stopwatch.GetTimestamp();
                await CaptureAsync();
                if (closing || token.IsCancellationRequested) break;
                var elapsedMs = (Stopwatch.GetTimestamp() - started) * 1000.0 / Stopwatch.Frequency;
                var remainingMs = (int)Math.Ceiling(1000d / captureFramesPerSecond - elapsedMs);
                if (remainingMs > 0)
                    await Task.Delay(remainingMs, token);
                else
                    await Task.Yield();
            }
        }
        catch (OperationCanceledException) when (closing) { }
        finally
        {
            // The final decode may still be running when the capture loop ends.
            // Observe its result even if shutdown has already closed the pipe.
            var pending = pendingJpegDecode;
            pendingJpegDecode = null;
            if (pending is not null)
                try { await pending; }
                catch (Exception) { /* Browser closed or navigation changed. */ }
        }
    }
    private async Task CaptureAsync()
    {
        if (closing || !captureReady || view.CoreWebView2 is null || DateTime.UtcNow < nextCaptureAttempt) return;
        try
        {
            if (++tick % 20 == 0) _ = UpdateVolumeAsync();
            if (screencastActive)
            {
                if (Volatile.Read(ref screencastLifetimeReceived) == 0 &&
                    Stopwatch.GetTimestamp() - screencastStarted > Stopwatch.Frequency * 10)
                    StopScreencastAndUsePreview();
                else
                {
                    return;
                }
            }
            MemoryStream? image = new();
            try
            {
                await view.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Jpeg, image);
                if (closing) return;

                // Keep at most one decode in flight. Reap the previous frame
                // after this capture so its decode overlaps the screenshot call.
                var previous = pendingJpegDecode;
                pendingJpegDecode = null;
                if (previous is not null) await previous;
                if (closing) return;
                var capturedImage = image;
                pendingJpegDecode = Task.Run(() =>
                {
                    using (capturedImage) return DecodeAndSend(capturedImage!);
                });
                image = null; // The worker owns and disposes this stream.
            }
            finally { image?.Dispose(); }
            if (captureHealth.Failures > 0)
            {
                if (captureHealth.Recovered()) transport.Send(7, []);

            }
        }
        catch (Exception e)
        {
            if (!closing)
            {
                nextCaptureAttempt = DateTime.UtcNow.AddMilliseconds(250);
                var report = captureHealth.Failed(DateTime.UtcNow);

                if (report) transport.Error("WebView2 frame capture failed: " + e.Message);
            }
        }
    }

    private async Task StartScreencastAsync()
    {
        try
        {
            var core = view.CoreWebView2;
            screencastReceiver = core.GetDevToolsProtocolEventReceiver("Page.screencastFrame");
            screencastReceiver.DevToolsProtocolEventReceived += OnScreencastFrame;
            await core.CallDevToolsProtocolMethodAsync("Page.startScreencast",
                JsonSerializer.Serialize(new { format = "jpeg", quality = 80, maxWidth = CaptureWidth,
                    maxHeight = CaptureHeight, everyNthFrame = 1, maxFramesInFlight = 2, sendLastFrame = true }));
            screencastStarted = Stopwatch.GetTimestamp();
            screencastActive = true;

        }
        catch (Exception) { StopScreencastAndUsePreview(); }
    }

    private void OnScreencastFrame(object? sender, CoreWebView2DevToolsProtocolEventReceivedEventArgs e)
    {
        if (closing || !screencastActive) return;
        try
        {
            using var document = JsonDocument.Parse(e.ParameterObjectAsJson);
            var root = document.RootElement;
            var sessionId = root.GetProperty("sessionId").GetInt32();
            var data = root.GetProperty("data").GetString();
            _ = AckScreencastFrameAsync(sessionId);
            if (string.IsNullOrEmpty(data)) return;
            Interlocked.Increment(ref screencastLifetimeReceived);
            lock (screencastLock)
            {
                pendingScreencastFrame = data;
                if (!screencastWorkerRunning)
                {
                    screencastWorkerRunning = true;
                    _ = Task.Run(DecodeScreencastFrames);
                }
            }
        }
        catch (Exception)
        {
            StopScreencastAndUsePreview();
        }
    }

    private async Task AckScreencastFrameAsync(int sessionId)
    {
        try
        {
            if (!closing) await view.CoreWebView2.CallDevToolsProtocolMethodAsync("Page.screencastFrameAck",
                JsonSerializer.Serialize(new { sessionId }));
        }
        catch (Exception)
        {
            if (!closing) StopScreencastAndUsePreview();
        }
    }

    private void DecodeScreencastFrames()
    {
        while (!closing && screencastActive)
        {
            string? data;
            lock (screencastLock)
            {
                data = pendingScreencastFrame;
                pendingScreencastFrame = null;
                if (data is null) { screencastWorkerRunning = false; return; }
            }
            try
            {
                using var image = new MemoryStream(Convert.FromBase64String(data));
                DecodeAndSend(image);
            }
            catch (Exception)
            {
                // Post the fallback to the WebView UI thread.
                if (!closing && IsHandleCreated)
                    BeginInvoke(() => StopScreencastAndUsePreview());
                break;
            }
        }
        lock (screencastLock) screencastWorkerRunning = false;
    }

    private void StopScreencastAndUsePreview()
    {
        if (screencastReceiver is not null)
        {
            screencastReceiver.DevToolsProtocolEventReceived -= OnScreencastFrame;
            screencastReceiver = null;
        }
        screencastActive = false;
        lock (screencastLock) pendingScreencastFrame = null;

    }

    private long DecodeAndSend(MemoryStream image)
    {
        var started = Stopwatch.GetTimestamp();
        image.Position = 0;
        using var bitmap = new Bitmap(image);
        using var frame = new Bitmap(960, 540, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(frame)) graphics.DrawImage(bitmap, 0, 0, 960, 540);
        var bits = frame.LockBits(new Rectangle(0, 0, 960, 540), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var bytes = new byte[960 * 540 * 4];
            for (var y = 0; y < 540; y++) Marshal.Copy(bits.Scan0 + y * bits.Stride, bytes, y * 960 * 4, 960 * 4);
            var decodeTicks = Stopwatch.GetTimestamp() - started;
            transport.Send(1, bytes);
            return decodeTicks;
        }
        finally { frame.UnlockBits(bits); }
    }
    private async Task UpdateVolumeAsync()
    {
        if (updatingVolume || closing) return;
        updatingVolume = true;
        try
        {
            var ids = view.CoreWebView2.Environment.GetProcessInfos().Select(p => p.ProcessId).ToHashSet();
            var level = Math.Clamp(volume / 100f, 0f, 1f);
            await Task.Run(() =>
            {
                using var devices = new MMDeviceEnumerator();
                foreach (var device in devices.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
                using (device)
                {
                    var sessions = device.AudioSessionManager.Sessions;
                    for (var i = 0; i < sessions.Count; i++)
                    using (var session = sessions[i])
                        if (ids.Contains((int)session.GetProcessID)) session.SimpleAudioVolume.Volume = level;
                }
            });
        }
        catch { /* A device or audio session can disappear during navigation. */ }
        finally { updatingVolume = false; }
    }
    private void QueueCommand(byte type, byte[] payload)
    {
        if (closing || IsDisposed) return;
        commands.Enqueue((type, payload));
        ScheduleCommandDrain();
    }
    private void ScheduleCommandDrain()
    {
        if (Interlocked.Exchange(ref commandDrainScheduled, 1) != 0) return;
        try { BeginInvoke(DrainCommands); }
        catch (InvalidOperationException) { Interlocked.Exchange(ref commandDrainScheduled, 0); }
    }
    private async void DrainCommands()
    {
        try
        {
            while (!closing && commands.TryDequeue(out var command))
            {
                // Adjacent moves supersede each other. Click/key/navigation
                // boundaries retain their order and never get discarded.
                if (command.Type == 15)
                    while (commands.TryPeek(out var next) && next.Type == 15 && commands.TryDequeue(out next)) command = next;
                await Dispatch(command.Type, command.Payload);
            }
        }
        finally
        {
            Interlocked.Exchange(ref commandDrainScheduled, 0);
            if (!closing && !commands.IsEmpty) ScheduleCommandDrain();
        }
    }
    private async Task MouseAsync(string type, int x, int y, string button = "none", int deltaX = 0, int deltaY = 0)
    {
        await RefreshInputViewportAsync();
        // The captured frame is 960x540 pixels, while DevTools input coordinates are CSS pixels.
        // On a 150% scaled display the WebView viewport may be only 640x360 CSS pixels.
        var cssX = Math.Clamp(x * inputViewportWidth / CaptureWidth, 0, inputViewportWidth - 1);
        var cssY = Math.Clamp(y * inputViewportHeight / CaptureHeight, 0, inputViewportHeight - 1);
        await view.CoreWebView2.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent", JsonSerializer.Serialize(new
        { type, x = cssX, y = cssY, button, buttons = mouseButtons,
            clickCount = type is "mousePressed" or "mouseReleased" ? 1 : 0, deltaX, deltaY }));
    }

    private async Task RefreshInputViewportAsync()
    {
        var now = Environment.TickCount64;
        if (inputViewportUpdatedAt != 0 && now - inputViewportUpdatedAt < 5000) return;
        inputViewportUpdatedAt = now;
        try
        {
            var response = await view.CoreWebView2.CallDevToolsProtocolMethodAsync("Page.getLayoutMetrics", "{}");
            using var metrics = JsonDocument.Parse(response);
            var root = metrics.RootElement;
            var viewport = root.TryGetProperty("cssVisualViewport", out var visual)
                ? visual : root.GetProperty("cssLayoutViewport");
            var width = viewport.GetProperty("clientWidth").GetDouble();
            var height = viewport.GetProperty("clientHeight").GetDouble();
            if (width > 0 && height > 0)
            {
                inputViewportWidth = width;
                inputViewportHeight = height;
            }
        }
        catch (Exception)
        {
            // During navigation, retain the last valid viewport until metrics are available again.
        }
    }
    private async Task Dispatch(byte type, byte[] payload)
    {
        try
        {
            var core = view.CoreWebView2;
            using var input = new BinaryReader(new MemoryStream(payload), Encoding.UTF8);
            switch (type)
            {
                case 10: var address = Encoding.UTF8.GetString(payload); navigationGuard.Permit(address, allowRedirects: true); core.Navigate(address); break;
                case 11: if (core.CanGoBack) { navigationGuard.PermitHistory(); core.GoBack(); } break;
                case 12: if (core.CanGoForward) { navigationGuard.PermitHistory(); core.GoForward(); } break;
                case 13: navigationGuard.Permit(core.Source); core.Reload(); break;
                case 14: core.Stop(); break;
                case 15:
                    var nextX = input.ReadInt32(); var nextY = input.ReadInt32();
                    if (nextX != mouseX || nextY != mouseY)
                    {
                        mouseX = nextX; mouseY = nextY;
                        await MouseAsync("mouseMoved", nextX, nextY,
                            (mouseButtons & 1) != 0 ? "left" : (mouseButtons & 2) != 0 ? "right" :
                            (mouseButtons & 4) != 0 ? "middle" : "none");
                    }
                    break;
                case 16:
                    var x = input.ReadInt32(); var y = input.ReadInt32();
                    var button = input.ReadInt32(); var up = input.ReadBoolean();
                    var mask = button == 2 ? 2 : button == 1 ? 4 : 1;
                    if (up) mouseButtons &= ~mask; else mouseButtons |= mask;
                    await MouseAsync(up ? "mouseReleased" : "mousePressed", x, y, button == 2 ? "right" : button == 1 ? "middle" : "left"); break;
                case 17:
                    var wheelX = input.ReadInt32(); var wheelY = input.ReadInt32();
                    var dx = input.ReadInt32(); var dy = input.ReadInt32();
                    await MouseAsync("mouseWheel", wheelX, wheelY, deltaX: -dx, deltaY: -dy); break;
                case 18:
                    var key = input.ReadInt32(); var keyUp = input.ReadBoolean();
                    var modifiers = input.ReadInt32();
                    var keyName = key switch
                    {
                        8 => "Backspace", 9 => "Tab", 13 => "Enter", 27 => "Escape", 32 => " ",
                        33 => "PageUp", 34 => "PageDown", 35 => "End", 36 => "Home", 37 => "ArrowLeft", 38 => "ArrowUp", 39 => "ArrowRight", 40 => "ArrowDown", 46 => "Delete",
                        >= 0x30 and <= 0x5A => ((char)key).ToString(), _ => "Unidentified"
                    };
                    await core.CallDevToolsProtocolMethodAsync("Input.dispatchKeyEvent", JsonSerializer.Serialize(new
                    { type = keyUp ? "keyUp" : "rawKeyDown", key = keyName, windowsVirtualKeyCode = key, nativeVirtualKeyCode = key,
                        modifiers = ((modifiers & 2) != 0 ? 8 : 0) | ((modifiers & 4) != 0 ? 2 : 0) | ((modifiers & 8) != 0 ? 1 : 0) })); break;
                case 19:
                    var character = input.ReadChar().ToString();
                    await core.CallDevToolsProtocolMethodAsync("Input.dispatchKeyEvent", JsonSerializer.Serialize(new { type = "char", text = character, unmodifiedText = character })); break;
                case 20: break; // Input is forwarded without taking focus from FFXIV.
                case 21: Close(); break;
                case 22: await ExportCookiesAsync(); break;
                case 23: volume = input.ReadInt32(); _ = UpdateVolumeAsync(); break;
                case 24: adBlock.Enabled = input.ReadBoolean(); navigationGuard.Permit(core.Source); core.Reload(); break;
                case 25: var destination = navigationGuard.Resolve(payload); if (destination is not null) core.Navigate(destination); break;
                case 26: navigationGuard.SetWhitelist(payload); break;
                case 27: navigationGuard.SetPreapprovedWhitelistEnabled(input.ReadBoolean()); break;
                case 28: _ = LookupYouTubeAccountAsync(Encoding.UTF8.GetString(payload)); break;
                case 29: captureFramesPerSecond = Math.Clamp(input.ReadInt32(), 15, 30); break;
            }
        }
        catch (Exception e) { if (!closing) transport.Error("WebView2 command failed: " + e.Message); }
    }
    private async Task ExportCookiesAsync()
    {
        var cookies = await view.CoreWebView2.CookieManager.GetCookiesAsync(null);
        var relevant = cookies.Where(c =>
            c.Domain.TrimStart('.').Equals("youtube.com", StringComparison.OrdinalIgnoreCase) ||
            c.Domain.EndsWith(".youtube.com", StringComparison.OrdinalIgnoreCase) ||
            c.Domain.TrimStart('.').Equals("google.com", StringComparison.OrdinalIgnoreCase) ||
            c.Domain.EndsWith(".google.com", StringComparison.OrdinalIgnoreCase))
            .Where(c => c.IsSession || c.Expires > DateTime.UtcNow).ToArray();
        var authenticated = relevant.Any(c => c.Name is "LOGIN_INFO" or "SID" or "SAPISID" or "__Secure-1PSID" or "__Secure-3PSID" or "__Secure-1PAPISID" or "__Secure-3PAPISID");
        var text = new StringBuilder("# Netscape HTTP Cookie File\n");
        static string Clean(string value) => value.Replace("\t", "").Replace("\r", "").Replace("\n", "");
        foreach (var cookie in relevant)
            text.Append(cookie.IsHttpOnly ? "#HttpOnly_" : "").Append(Clean(cookie.Domain)).Append('\t')
                .Append(cookie.Domain.StartsWith('.') ? "TRUE" : "FALSE").Append('\t').Append(Clean(cookie.Path)).Append('\t')
                .Append(cookie.IsSecure ? "TRUE" : "FALSE").Append('\t').Append(cookie.IsSession ? 0 : new DateTimeOffset(cookie.Expires).ToUnixTimeSeconds()).Append('\t')
                .Append(Clean(cookie.Name)).Append('\t').Append(Clean(cookie.Value)).Append('\n');
        transport.Send(5, System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new
        { Success = authenticated, Cookies = authenticated ? text.ToString() : null, Error = authenticated ? null : "No signed-in YouTube session was found. Finish signing in, then try again." }));
    }
    private async Task LookupYouTubeAccountAsync(string requestId)
    {
        var core = view.CoreWebView2;
        var result = await YouTubeAccountLookup.RunAsync(requestId,
            () => core.Source, () => loading,
            address => { navigationGuard.Permit(address, allowRedirects: true); core.Navigate(address); },
            async script =>
            {
                var json = await core.ExecuteScriptAsync(script);
                return JsonSerializer.Deserialize<string>(json);
            });
        transport.Send(9, JsonSerializer.SerializeToUtf8Bytes(result));
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            closing = true;
            captureLoopCancellation.Cancel();
            captureLoopCancellation.Dispose(); recorder?.Dispose(); view.Dispose();
        }
        base.Dispose(disposing);
    }
}

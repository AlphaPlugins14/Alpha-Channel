using System.Diagnostics;
using Dalamud.Hooking;
using SharpDX.Direct3D11;
using GfxKernel = FFXIVClientStructs.FFXIV.Client.Graphics.Kernel;

namespace AlphaChannel.Plugin.Video;

// Locate native UI composition through the final colour targets.
// Missing boundaries retain the original renderer rather than using stale pixels.
internal static class TvUiRenderer
{
    private static readonly object gate = new();
    private static readonly TvUiRendererPolicy policy = new();
    private static readonly TvUiRendererContext contextGuard = new();
    private static readonly TvUiRendererTargets targetClassifier = new();
    private static readonly TvUiRendererInputs finalInputs = new();
    private static readonly TvUiRendererCapture sceneCapture = new();
    private static readonly List<(Action Enable, Action Disable, Action Dispose)> hooks = [];
    private static readonly Dictionary<nint, int> hookedRoles = [];
    private static readonly int[] methodSlots = TvUiRendererMethods.Slots;
    private static Func<nint, ShaderResourceView?, bool>? draw;
    private static Func<bool>? available, keepUiVisible;
    private static PixelShader? boundaryShader, finalShader;
    private static bool enabled, refreshQueued, matchedThisFrame, restoredThisFrame;
    private static volatile bool stopping;
    private static bool requested = true, faulted, shutdownDrained;
    private static Action? releaseTargets;
    private static int callbacks, missedFrames;
    private static nint contextPointer;
    private static long frames, matchedFrames, earlyFrames, capturedFrames, restoredFrames, fallbackFrames;
    private static long observedDraws, fastRejected, uncoveredFrames, coverageRepairs, modeChanges, commandLists;
    private static long callbackTicks, restoreTicks, modeVisibleFrames, modeCoveredFrames;
    private static string error = "", renderInfo = "No TV drawn yet.";
    private static DateTime started;
    [ThreadStatic] private static bool inside;
    [ThreadStatic] private static int presentDepth;

    internal sealed record Snapshot(bool Enabled, bool Requested, bool KeepUiVisible, string Error, string Summary);
    internal static void Attach(Func<nint, ShaderResourceView?, bool> callback, Func<bool> ready, Func<bool> uiSetting, Action release)
    { lock (gate) { draw = callback; releaseTargets = release; shutdownDrained = false; available = ready; keepUiVisible = uiSetting; stopping = false; requested = true; faulted = false; ResetCounters(); } }
    internal static void BeginPresent() => presentDepth++;
    internal static void EndPresent() => presentDepth--;
    internal static TvUiRendererContext.Scope EnterContext() => contextGuard.Enter();
    private static TvUiRendererContext.Scope EnterDraw(nint pointer) =>
        inside || presentDepth > 0 || pointer != contextPointer || !Volatile.Read(ref enabled) || stopping
            ? default : contextGuard.Enter();

    private static void UpdateUiMode()
    {
        if (!policy.SetUiVisibility(keepUiVisible?.Invoke() != false)) return;
        modeChanges++; sceneCapture.Dispose(); restoredThisFrame = false;
    }
    internal static bool DrawAtPresent()
    {
        lock (gate)
        {
            if (stopping || faulted || !requested) return false;
            try
            {
                if (available?.Invoke() != true)
                {
                    if (enabled) StopRendering();
                    return false;
                }
                if (!enabled) StartRendering();
                UpdateUiMode();
            }
            catch (Exception ex) { Fail(ex); return false; }
            if (!policy.CoverUi) return policy.Drawn;
            if (!sceneCapture.Fresh || draw == null || restoredThisFrame) return false;
            var time = Stopwatch.GetTimestamp();
            try
            {
                var success = TvUiRendererState.Draw(DxHandler.Device!.ImmediateContext, () => draw(0, sceneCapture.View));
                if (success) { restoredFrames++; restoredThisFrame = true; }
                return success;
            }
            catch (Exception ex) { Fail(ex); return false; }
            finally { restoreTicks += Stopwatch.GetTimestamp() - time; }
        }
    }
    internal static bool CaptureMatches(uint width, uint height) => sceneCapture.Fresh && sceneCapture.Width == width && sceneCapture.Height == height;

    // All transitions run within Present's complete context batch. The debug
    // override changes only this session; normal builds always request the path.
    internal static void Configure(bool turnOn)
    {
        DxHandler.RunOnRenderThread("AlphaChannel.TvUiRenderer", () =>
        {
            lock (gate)
            {
                if (stopping) return;
                requested = turnOn; faulted = false;
                try
                {
                    if (!turnOn) StopRendering();
                    else if (!enabled) ResetCounters();
                }
                catch (Exception ex) { Fail(ex); }
            }
        });
    }
    private static void StartRendering()
    {
        var before = ReadDrawEntries();
        contextGuard.Enable(DxHandler.Device!);
        Install(before, ReadDrawEntries());
        UpdateUiMode(); policy.EndFrame();
        enabled = policy.Enabled = true;
        foreach (var hook in hooks) hook.Enable();
    }
    private static void StopRendering()
    {
        enabled = policy.Enabled = false; policy.EndFrame();
        foreach (var hook in hooks) hook.Disable();
        finalInputs.Clear(); targetClassifier.Clear(); sceneCapture.Dispose(); ClearShaderCache();
        matchedThisFrame = restoredThisFrame = false;
        releaseTargets?.Invoke();
        contextGuard.Disable();
    }
    internal static void OnDeviceFailure()
    {
        DxHandler.RunOnRenderThread("AlphaChannel.TvUiRenderer.DeviceFailure", () =>
        {
            lock (gate)
            {
                if (stopping) return;
                Fail(new InvalidOperationException("Graphics device unavailable."));
            }
        }, allowAfterDeviceLoss: true);
    }

    private delegate void DrawIndexed(nint context, uint count, uint start, int baseVertex);
    private delegate void Draw(nint context, uint count, uint start);
    private delegate void DrawIndexedInstanced(nint context, uint count, uint instances, uint start, int baseVertex, uint firstInstance);
    private delegate void DrawInstanced(nint context, uint count, uint instances, uint start, uint firstInstance);
    private delegate void DrawIndirect(nint context, nint argumentBuffer, uint offset);
    private delegate void DrawAuto(nint context);
    private delegate void ExecuteCommandList(nint context, nint commandList, int restoreState);

    private static unsafe nint[] ReadDrawEntries()
    {
        var table = *(nint**)DxHandler.Device!.ImmediateContext.NativePointer;
        return methodSlots.Select(slot => table[slot]).ToArray();
    }

    private static void Install(nint[] before, nint[] after)
    {
        var context = DxHandler.Device!.ImmediateContext;
        contextPointer = context.NativePointer;
        if (before.Distinct().Count() != methodSlots.Length || after.Distinct().Count() != methodSlots.Length || before.Concat(after).Any(p => p == 0) ||
            before.Where((p, i) => after.Where((_, j) => i != j).Contains(p)).Any())
            throw new NotSupportedException("Draw entry points are not distinct.");
        // ID3D11DeviceContext slots from the Windows SDK. Only this exact
        // immediate context is inspected; other contexts forward untouched.
        for (var role = 0; role < methodSlots.Length; role++)
        foreach (var address in new[] { before[role], after[role] }.Distinct())
        {
        if (hookedRoles.TryGetValue(address, out var existingRole))
        {
            if (existingRole != role) throw new NotSupportedException("An entry point is shared by incompatible methods.");
            continue;
        }
        if (hooks.Count >= 64) throw new NotSupportedException("Hook coverage limit reached.");
        var entryRole = role;
        if (role == 0)
        {
        Hook<DrawIndexed>? indexed = null;
        indexed = Plugin.InteropProvider.HookFromAddress<DrawIndexed>(address, (c, n, s, b) =>
        { Interlocked.Increment(ref callbacks); try { using var entry = TvUiRendererReentry.Enter(); using var scope = entry.Outermost ? EnterDraw(c) : default; if (entry.Outermost) Probe(c, entryRole); indexed!.Original(c, n, s, b); } finally { Interlocked.Decrement(ref callbacks); } });
        hooks.Add((indexed.Enable, indexed.Disable, indexed.Dispose));
        }
        else if (role == 1)
        {
        Hook<Draw>? plain = null;
        plain = Plugin.InteropProvider.HookFromAddress<Draw>(address, (c, n, s) =>
        { Interlocked.Increment(ref callbacks); try { using var entry = TvUiRendererReentry.Enter(); using var scope = entry.Outermost ? EnterDraw(c) : default; if (entry.Outermost) Probe(c, entryRole); plain!.Original(c, n, s); } finally { Interlocked.Decrement(ref callbacks); } });
        hooks.Add((plain.Enable, plain.Disable, plain.Dispose));
        }
        else if (role == 2)
        {
        Hook<DrawIndexedInstanced>? indexedInstances = null;
        indexedInstances = Plugin.InteropProvider.HookFromAddress<DrawIndexedInstanced>(address, (c, n, i, s, b, f) =>
        { Interlocked.Increment(ref callbacks); try { using var entry = TvUiRendererReentry.Enter(); using var scope = entry.Outermost ? EnterDraw(c) : default; if (entry.Outermost) Probe(c, entryRole); indexedInstances!.Original(c, n, i, s, b, f); } finally { Interlocked.Decrement(ref callbacks); } });
        hooks.Add((indexedInstances.Enable, indexedInstances.Disable, indexedInstances.Dispose));
        }
        else if (role == 3)
        {
        Hook<DrawInstanced>? instances = null;
        instances = Plugin.InteropProvider.HookFromAddress<DrawInstanced>(address, (c, n, i, s, f) =>
        { Interlocked.Increment(ref callbacks); try { using var entry = TvUiRendererReentry.Enter(); using var scope = entry.Outermost ? EnterDraw(c) : default; if (entry.Outermost) Probe(c, entryRole); instances!.Original(c, n, i, s, f); } finally { Interlocked.Decrement(ref callbacks); } });
        hooks.Add((instances.Enable, instances.Disable, instances.Dispose));
        }
        else if (role is 4 or 5)
        {
        Hook<DrawIndirect>? indirect = null;
        indirect = Plugin.InteropProvider.HookFromAddress<DrawIndirect>(address, (c, buffer, offset) =>
        { Interlocked.Increment(ref callbacks); try { using var entry = TvUiRendererReentry.Enter(); using var scope = entry.Outermost ? EnterDraw(c) : default; if (entry.Outermost) Probe(c, entryRole); indirect!.Original(c, buffer, offset); } finally { Interlocked.Decrement(ref callbacks); } });
        hooks.Add((indirect.Enable, indirect.Disable, indirect.Dispose));
        }
        else if (role == 6)
        {
        Hook<DrawAuto>? auto = null;
        auto = Plugin.InteropProvider.HookFromAddress<DrawAuto>(address, c =>
        { Interlocked.Increment(ref callbacks); try { using var entry = TvUiRendererReentry.Enter(); using var scope = entry.Outermost ? EnterDraw(c) : default; if (entry.Outermost) Probe(c, entryRole); auto!.Original(c); } finally { Interlocked.Decrement(ref callbacks); } });
        hooks.Add((auto.Enable, auto.Disable, auto.Dispose));
        }
        else
        {
        Hook<ExecuteCommandList>? execute = null;
        execute = Plugin.InteropProvider.HookFromAddress<ExecuteCommandList>(address, (c, list, restore) =>
        { Interlocked.Increment(ref callbacks); try { using var scope = EnterDraw(c); Probe(c, entryRole); execute!.Original(c, list, restore); } finally { Interlocked.Decrement(ref callbacks); } });
        hooks.Add((execute.Enable, execute.Disable, execute.Dispose));
        }
        hookedRoles.Add(address, role);
        }
    }

    private static unsafe void Probe(nint pointer, int method)
    {
        if (inside || presentDepth > 0 || !Volatile.Read(ref enabled) || stopping ||
            !DxHandler.CanUploadVideoFrames || pointer != contextPointer) return;
        var time = Stopwatch.GetTimestamp();
        inside = true;
        try
        {
            lock (gate)
            {
                if (!enabled || stopping) return;
                if (method == 7) { commandLists++; return; }
                observedDraws++;
                var ctx = DxHandler.Device!.ImmediateContext;
                var filterReady = boundaryShader != null && finalShader != null;
                // Without a learned filter there is nothing to reject cheaply.
                // Retain the availability check first while idle/discovering.
                if (!filterReady && available?.Invoke() != true) return;
                using var shader = TvUiRendererShader.Get(ctx.NativePointer);
                if (shader.Pointer == 0) return;
                // Keep owning references, so cached shader addresses cannot be
                // recycled. Re-discover after missed frames or coverage changes.
                if (filterReady &&
                    shader.Pointer != boundaryShader!.NativePointer && shader.Pointer != finalShader!.NativePointer)
                { fastRejected++; return; }
                if (filterReady && available?.Invoke() != true) return;
                UpdateUiMode();
                var device = GfxKernel.Device.Instance();
                if (device == null || device->SwapChain == null || device->SwapChain->BackBuffer == null ||
                    device->SwapChain->BackBuffer->MipRenderTargets == null) return;
                var target = (nint)device->SwapChain->BackBuffer->MipRenderTargets[0].D3D11RenderTargetViewOrDepthStencilView;
                finalInputs.SetOutputSize((int)device->SwapChain->Width, (int)device->SwapChain->Height);
                var targets = ctx.OutputMerger.GetRenderTargets(1, out var dsv);
                try
                {
                    if (targets.Length == 0 || targets[0] == null) return;
                    var info = targetClassifier.Classify(targets[0], target);
                    var linked = !info.Compatible && info.FullColourView && finalInputs.TryFind(info.Resource, out _);
                    if (!info.Compatible && !linked) return;
                    if (info.Compatible)
                    {
                        finalInputs.Capture(ctx);
                        RememberShader(ref finalShader, shader.Pointer);
                    }
                    if (policy.Attempted) return;
                    using var depth = ctx.OutputMerger.GetDepthStencilState(out _);
                    if (dsv != null && (depth == null || depth.Description.IsDepthEnabled)) return;
                    using var blend = ctx.OutputMerger.GetBlendState(out _, out _);
                    if (blend == null || !blend.Description.RenderTarget[0].IsBlendEnabled) return;
                    matchedThisFrame = true;
                    RememberShader(ref boundaryShader, shader.Pointer);
                    if (draw == null || !policy.ShouldAttempt()) return;
                    var success = policy.CoverUi
                        ? sceneCapture.Capture(DxHandler.Device!, ctx, targets[0])
                        : TvUiRendererState.Draw(ctx, () => draw(targets[0].NativePointer, null));
                    policy.CompleteAttempt(success);
                    if (success) { if (policy.CoverUi) capturedFrames++; else earlyFrames++; }
                }
                finally { foreach (var view in targets) view?.Dispose(); dsv?.Dispose(); }
            }
        }
        catch (Exception ex) { lock (gate) Fail(ex); }
        finally { inside = false; Interlocked.Add(ref callbackTicks, Stopwatch.GetTimestamp() - time); }
    }
    private static void RememberShader(ref PixelShader? stored, nint shader)
    {
        if (stored?.NativePointer == shader) return;
        System.Runtime.InteropServices.Marshal.AddRef(shader);
        stored?.Dispose(); stored = new PixelShader(shader);
    }
    private static void ClearShaderCache()
    { boundaryShader?.Dispose(); boundaryShader = null; finalShader?.Dispose(); finalShader = null; missedFrames = 0; }

    // Called before DXGI resizes the game's buffers, while our context batch is
    // held and new TV draws/uploads are suspended. Keep playback and hooks alive.
    internal static void InvalidateOutput()
    {
        lock (gate)
        {
            policy.EndFrame(); matchedThisFrame = restoredThisFrame = false;
            finalInputs.Clear(); targetClassifier.Clear(); sceneCapture.Dispose(); ClearShaderCache();
            releaseTargets?.Invoke();
            renderInfo = "Output changed; waiting for fresh render targets.";
        }
    }

    internal static void EndFrame()
    {
        lock (gate)
        {
            targetClassifier.Clear();
            // Game-owned views must not pin a backbuffer between Presents,
            // including when the boundary path fell back to the regular painter.
            releaseTargets?.Invoke();
            if (!enabled) return;
            finalInputs.EndFrame(); frames++;
            if (policy.CoverUi) modeCoveredFrames++; else modeVisibleFrames++;
            if (matchedThisFrame) { matchedFrames++; missedFrames = 0; }
            else if (++missedFrames >= 2) ClearShaderCache();
            if (policy.CoverUi ? !restoredThisFrame : !policy.Drawn) fallbackFrames++;
            sceneCapture.EndFrame(); restoredThisFrame = matchedThisFrame = false; policy.EndFrame();
            var entries = ReadDrawEntries();
            if (entries.Where((p, i) => !hookedRoles.TryGetValue(p, out var role) || role != i).Any())
            {
                uncoveredFrames++;
                if (!refreshQueued)
                {
                    refreshQueued = true;
                    DxHandler.RunOnRenderThread("AlphaChannel.TvUiRenderer.Coverage", RepairCoverage);
                }
            }
        }
    }
    private static void RepairCoverage()
    {
        lock (gate)
        {
            refreshQueued = false;
            if (!enabled || stopping) return;
            try
            {
                var entries = ReadDrawEntries(); Install(entries, entries);
                foreach (var hook in hooks) hook.Enable();
                coverageRepairs++; ClearShaderCache();
            }
            catch (Exception ex) { Fail(ex); }
        }
    }
    internal static void NoteRender(uint width, uint height, uint depthWidth, uint depthHeight, bool sampledDepth, int rects)
    { lock (gate) { if (enabled) renderInfo = $"Output {width}x{height}; scene depth {depthWidth}x{depthHeight}; {(sampledDepth ? "sampled depth SRV" : "depth attachment")}; UI mask rects {rects}"; } }

    private static void Fail(Exception ex)
    {
        faulted = true; enabled = policy.Enabled = false; policy.EndFrame();
        error = $"TV renderer stopped: {ex.GetType().Name} (0x{ex.HResult:X8}). Original rendering restored.";
        foreach (var hook in hooks) { try { hook.Disable(); } catch { } }
        try { contextGuard.Disable(); } catch { }
        finalInputs.Clear(); targetClassifier.Clear(); sceneCapture.Dispose(); ClearShaderCache();
        releaseTargets?.Invoke();
        AepLog.Warning(error);
        if (GraphicsDeviceFailure.IsDeviceError(ex.HResult))
            DxHandler.ReportDeviceFailure(ex.HResult, GraphicsDeviceFailure.Source.OnPresentSubscriber);
    }
    private static void ResetCounters()
    {
        frames = matchedFrames = earlyFrames = capturedFrames = restoredFrames = fallbackFrames = 0;
        observedDraws = fastRejected = uncoveredFrames = coverageRepairs = modeChanges = commandLists = 0;
        callbackTicks = restoreTicks = modeVisibleFrames = modeCoveredFrames = 0;
        matchedThisFrame = restoredThisFrame = false; refreshQueued = false;
        finalInputs.Clear(); targetClassifier.Clear(); sceneCapture.Dispose(); ClearShaderCache();
        error = ""; renderInfo = "No TV drawn yet."; started = DateTime.UtcNow;
    }
    private static string Summary() =>
        $"Frames: {frames}; boundary found: {matchedFrames}; original-renderer fallback: {fallbackFrames}\n" +
        $"UI above TV: {modeVisibleFrames} frames; early TV draws: {earlyFrames}\n" +
        $"TV covers UI: {modeCoveredFrames} frames; scene captures: {capturedFrames}; restores plus TV: {restoredFrames}\n" +
        $"UI setting changes: {modeChanges}; automatic hook repairs: {coverageRepairs}; uncovered frames: {uncoveredFrames}; hooks: {hooks.Count}\n" +
        $"Observed calls: {observedDraws}; shader fast rejects: {fastRejected}; command lists: {commandLists}\n" +
        $"CPU hook work: {Milliseconds(callbackTicks):F3} ms/frame; Present restore: {Milliseconds(restoreTicks):F3} ms/frame (CPU submission only; excludes context-lock waits/GPU time)\n" +
        $"{renderInfo}\n{contextGuard.Status}";
    private static double Milliseconds(long ticks) => frames == 0 ? 0 : ticks * 1000.0 / Stopwatch.Frequency / frames;
    internal static Snapshot Read()
    { lock (gate) return new(enabled, requested, keepUiVisible?.Invoke() != false, error, Summary()); }
    private static string BuildName =>
#if DEBUG
        "Debug";
#else
        "Release";
#endif
    internal static string Report()
    {
        lock (gate) return $"Alpha Channel TV renderer integrated v1 / {typeof(TvUiRenderer).Assembly.GetName().Version} / {BuildName}\n" +
            $"Started UTC: {started:O}; copied UTC: {DateTime.UtcNow:O}\nRequested: {requested}; active: {enabled}; faulted: {faulted}; Keep game UI visible over TV: {keepUiVisible?.Invoke() != false}\n" +
            Summary() + $"\n{finalInputs.Description}\n{error}\n" +
            "Statistics only. Visual correctness and compatibility require in-game testing. Missing boundaries use the original renderer; the original UI leak may then recur.\n";
    }

    internal static void PauseForShutdown() => stopping = true;

    internal static async Task ShutdownAsync()
    {
        lock (gate)
        {
            stopping = true; requested = false; enabled = false; policy.Enabled = false; policy.EndFrame(); draw = null; available = keepUiVisible = null;
            var disabled = true;
            foreach (var hook in hooks)
                try { hook.Disable(); }
                catch { disabled = false; }
            if (!disabled)
            {
                throw new InvalidOperationException("A draw hook could not be disabled safely.");
            }
        }
        // BeginShutdown runs before the painter is destroyed. Forwarding calls
        // still need their trampolines until the driver has returned.
        while (Volatile.Read(ref callbacks) != 0)
            await Task.Delay(10).ConfigureAwait(false);
        contextGuard.Disable();
        lock (gate)
        {
            targetClassifier.Clear();
            finalInputs.Clear();
            sceneCapture.Dispose();
            foreach (var hook in hooks)
                try { hook.Dispose(); }
                catch { AepLog.Warning("[TV renderer] Draw hook cleanup failed."); }
            hooks.Clear();
            hookedRoles.Clear();
            ClearShaderCache();
            releaseTargets = null; shutdownDrained = true;
        }
    }

    // Called only after DxHandler has disabled and drained Present too. A
    // Present callback may already have read the guard's COM wrapper when
    // Shutdown runs, so its native interface must remain alive until then.
    internal static void ReleaseContext()
    {
        if (!shutdownDrained || Volatile.Read(ref callbacks) != 0)
        {
            AepLog.Warning("[TV renderer] Retaining context protection while draw-hook shutdown is incomplete.");
            return;
        }
        contextGuard.Dispose();
    }
}

using System.Collections.Concurrent;
using Dalamud.Hooking;
using Dalamud.Plugin;
using D3D11 = SharpDX.Direct3D11;
using GfxKernel = FFXIVClientStructs.FFXIV.Client.Graphics.Kernel;

namespace AlphaChannel.Plugin.Video;

internal static class DxHandler
{
	internal static D3D11.Device? Device { get; private set; }

	private static readonly ConcurrentDictionary<string, Action> _pendingRenderWork = new();
	private static readonly ConcurrentQueue<Action> _shutdownRenderWork = new();
	private static readonly ManualResetEventSlim _presentIdle = new(true);
	private static readonly object _presentLifetimeLock = new();
	private static int _activePresentCallbacks;
	private static int _renderThreadId;
    private static int _disposeStarted;
    private static Semaphore? _graphicsLease;
	private static volatile bool _shuttingDown;
    private static volatile bool _gameForeground = true;
    internal static bool CanUploadVideoFrames => !_shuttingDown && !_deviceFailure.Failed && Volatile.Read(ref _resizeDepth) == 0;
    private static readonly GraphicsDeviceFailure _deviceFailure = new();
    private static long _nextDeviceCheck, _nextFocusCheck;

	//Fires every Present call, unlike RunOnRenderThread's one-shot queue - for subscribers that need to
	//redraw every frame (e.g. a world-space overlay) rather than react to a single freshly-produced frame.
	internal static event Action? OnPresent;

	private unsafe delegate int PresentDelegate(void* swapChain, uint syncInterval, uint flags);
	private static Hook<PresentDelegate>? _presentHook;
    private static PresentDelegate? _presentOriginal;
    private static NativeHookRelay<PresentDelegate>? _presentRelay;
    private unsafe delegate int ResizeBuffersDelegate(void* swapChain, uint count, uint width, uint height, int format, uint flags);
    private static Hook<ResizeBuffersDelegate>? _resizeHook;
    private static ResizeBuffersDelegate? _resizeOriginal;
    private static NativeHookRelay<ResizeBuffersDelegate>? _resizeRelay;
    private static nint _swapChainPointer;
    private static int _resizeDepth;

    internal sealed class PreviousGraphicsCleanupException : InvalidOperationException;

	internal static void Initialise(IDalamudPluginInterface pluginInterface, bool onGameThread = false)
	{
		if (Device is not null || _presentHook is not null)
		{
			throw new InvalidOperationException("The DirectX handler is already initialized.");
		}

        // Dalamud can load a new copy of this assembly before deferred cleanup
        // from the previous copy has finished restoring the game's context.
        var lease = new Semaphore(1, 1, $@"Local\AlphaChannel.Graphics.{Environment.ProcessId}");
        if (!lease.WaitOne(0))
        {
            lease.Dispose();
            throw new PreviousGraphicsCleanupException();
        }
        _graphicsLease = lease;
		_shuttingDown = false;
        _disposeStarted = 0;
        _gameForeground = true;
        _deviceFailure.Reset();
        _nextDeviceCheck = 0;
        PlaybackRenderDiagnostics.Reset();
		_renderThreadId = onGameThread ? Environment.CurrentManagedThreadId : 0;
        _resizeDepth = 0;
		try
        {
            var device = new D3D11.Device(pluginInterface.UiBuilder.DeviceHandle);
            // Own a reference to the device borrowed from Dalamud.
            ((SharpDX.IUnknown)device).AddReference();
            Device = device;
            HookPresent();
        }
		catch
		{
			Dispose();
			throw;
		}
	}

	//Queues 'work' to run once, synchronously, from inside the game's own Present call - the only point at
	//which touching Device.ImmediateContext from outside the game's own render thread is safe.
	//A newer call for the same key overwrites an older, not-yet-run one, since only the latest frame matters.
	internal static void RunOnRenderThread(string key, Action work, bool allowAfterDeviceLoss = false)
	{
        if (_shuttingDown || (_deviceFailure.Failed && !allowAfterDeviceLoss)) return;
        _pendingRenderWork[key] = () => { if (!_deviceFailure.Failed || allowAfterDeviceLoss) work(); };
	}

    // Final disposal is owned by graphics teardown. It can outlive the loader's
    // wait, but must never run alongside an admitted Present or draw callback.
    internal static void QueueShutdownWork(Action work) => _shutdownRenderWork.Enqueue(work);

	internal static void CancelRenderThreadWork(string key)
	{
		_pendingRenderWork.TryRemove(key, out _);
	}

	// Plugin construction and destruction are performed by Dalamud's loader
	// thread. D3D11 resource creation/destruction is instead executed inside
	// the game's Present call so it cannot race the immediate context or the
	// NVIDIA driver's render worker during a development-plugin reload.
	internal static T RunOnRenderThreadAndWait<T>(
		string key,
		Func<T> work,
		TimeSpan? timeout = null, bool allowAfterDeviceLoss = false)
	{
		ArgumentNullException.ThrowIfNull(work);

		if (Environment.CurrentManagedThreadId == Volatile.Read(ref _renderThreadId))
		{
            if (_deviceFailure.Failed && !allowAfterDeviceLoss)
                throw new InvalidOperationException("The game's graphics device has failed; new render resources cannot be created.");
			return work();
		}

		if (_presentHook is null)
		{
			throw new InvalidOperationException("The DirectX Present hook is not initialized.");
		}

		using var completed = new ManualResetEventSlim(false);
		T? result = default;
		Exception? failure = null;
		var state = 0;

		_pendingRenderWork[key] = () =>
		{
			// A timed-out caller must never leave work behind that can execute
			// after its plugin instance has already been unloaded.
			if (Interlocked.CompareExchange(ref state, 1, 0) != 0)
			{
				return;
			}

			try
			{
                if (_deviceFailure.Failed && !allowAfterDeviceLoss)
                    throw new InvalidOperationException("The game's graphics device has failed; new render resources cannot be created.");
				result = work();
			}
			catch (Exception exception)
			{
                if (GraphicsDeviceFailure.IsDeviceError(exception.HResult))
                    ReportDeviceFailure(exception.HResult, GraphicsDeviceFailure.Source.RenderThreadWork);
				failure = exception;
			}
			finally
			{
				Volatile.Write(ref state, 2);
				completed.Set();
			}
		};

		var waitTime = timeout ?? TimeSpan.FromSeconds(10);
		if (!completed.Wait(waitTime))
		{
			if (Interlocked.CompareExchange(ref state, 3, 0) == 0)
			{
				_pendingRenderWork.TryRemove(key, out _);
				throw new TimeoutException(
					$"Timed out waiting for DirectX render-thread work '{key}'.");
			}

			// The render thread already owns the callback. It is safer to wait
			// for it than to unload code or resources while it is executing.
			completed.Wait();
		}

		if (failure is not null)
		{
			throw new InvalidOperationException(
				$"DirectX render-thread work '{key}' failed.",
				failure);
		}

		return result!;
	}

	internal static void RunOnRenderThreadAndWait(
		string key,
		Action work,
		TimeSpan? timeout = null, bool allowAfterDeviceLoss = false)
	{
		RunOnRenderThreadAndWait(
			key,
			() =>
			{
				work();
				return true;
			},
			timeout, allowAfterDeviceLoss);
	}

	internal static void BeginShutdown()
	{
		_shuttingDown = true;
        if (Device is not null) TvUiRenderer.PauseForShutdown();
	}

	private static unsafe void HookPresent()
	{
		var gameDevice = GfxKernel.Device.Instance();
        if (gameDevice == null || gameDevice->SwapChain == null || gameDevice->SwapChain->DXGISwapChain == null)
            throw new InvalidOperationException("The game's DirectX swap chain is not available.");
		nint swapChainPtr = (nint)gameDevice->SwapChain->DXGISwapChain;
		nint* vtable = *(nint**)swapChainPtr;
        if (vtable == null || vtable[8] == 0 || vtable[13] == 0)
            throw new InvalidOperationException("The game's DirectX swap-chain methods are not available.");

        // Hook the pointer slots, not the code they point at. Overlay/driver
        // thunks may not be relocatable by Reloaded's instruction decoder.
        // Dalamud retains forwarding stubs when disabled so later hooks can
        // still forward through this chain after our plugin is unloaded.
        _swapChainPointer = swapChainPtr;
        // Never publish our managed callback directly into the hook chain.
        // A later hook may retain its forwarding target beyond our unload.
        _presentRelay = new(vtable[8], PresentDetour);
        _presentHook = Plugin.InteropProvider.HookFromFunctionPointerVariable<PresentDelegate>((nint)(vtable + 8), _presentRelay.EntryPoint);
        _presentOriginal = _presentHook.Original;
        _presentRelay.BindOriginal(_presentOriginal);
        _resizeRelay = new(vtable[13], ResizeBuffersDetour, stackArguments: 2);
        _resizeHook = Plugin.InteropProvider.HookFromFunctionPointerVariable<ResizeBuffersDelegate>((nint)(vtable + 13), _resizeRelay.EntryPoint);
        _resizeOriginal = _resizeHook.Original;
        _resizeRelay.BindOriginal(_resizeOriginal);
        _presentRelay.Enable();
        _resizeRelay.Enable();
        _resizeHook.Enable();
		_presentHook.Enable();
        AepLog.DirectXPointerHooksReady();
	}

    private static unsafe int ResizeBuffersDetour(void* swapChain, uint count, uint width, uint height, int format, uint flags)
    {
        ResizeBuffersDelegate original;
        // Share Present's drain counter: neither trampoline can be freed while
        // a graphics callback is still forwarding into DXGI/the next hook.
        lock (_presentLifetimeLock)
        {
            _activePresentCallbacks++; _presentIdle.Reset(); original = _resizeOriginal!;
        }
        var ownSwapChain = (nint)swapChain == _swapChainPointer;
        if (ownSwapChain) Interlocked.Increment(ref _resizeDepth);
        try
        {
            if (ownSwapChain && !_shuttingDown)
            {
                // Drain our current context batch before releasing its views.
                // Do not hold the context lock through the game's resize call.
                try
                {
                    using var scope = TvUiRenderer.EnterContext();
                    TvUiRenderer.InvalidateOutput();
                }
                catch (Exception) { AepLog.Warning("[DxHandler] TV resize cleanup failed; forwarding the game resize."); }
            }
            int result;
            try { result = original(swapChain, count, width, height, format, flags); }
            catch (Exception exception) when (GraphicsDeviceFailure.IsDeviceError(exception.HResult))
            { result = exception.HResult; }
            if (ownSwapChain && GraphicsDeviceFailure.IsDeviceError(result))
                ReportDeviceFailure(result, GraphicsDeviceFailure.Source.ResizeResult);
            return result;
        }
        finally
        {
            if (ownSwapChain) Interlocked.Decrement(ref _resizeDepth);
            lock (_presentLifetimeLock)
            {
                if (--_activePresentCallbacks == 0) _presentIdle.Set();
            }
        }
    }

	private static unsafe int PresentDetour(void* swapChain, uint syncInterval, uint flags)
	{
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
		PresentDelegate original;
		lock (_presentLifetimeLock)
		{
			// Updating the count and signal separately lets the previous
			// callback signal idle after a new callback has already entered.
			_activePresentCallbacks++;
			_presentIdle.Reset();
			original = _presentOriginal!;
		}

        var ownSwapChain = (nint)swapChain == _swapChainPointer;
        var beganPresent = false;
		try
		{
            // The vtable can be shared with other swap chains. Always drain
            // those callbacks during shutdown, but only render/poll our game.
            if (!ownSwapChain || _shuttingDown) return original(swapChain, syncInterval, flags);
            TvUiRenderer.BeginPresent();
            beganPresent = true;
            Volatile.Write(ref _renderThreadId, Environment.CurrentManagedThreadId);
            var focusTime = Environment.TickCount64;
            var foreground = _gameForeground;
            if (focusTime >= _nextFocusCheck) { _nextFocusCheck = focusTime + 100; foreground = GameWindowFocus.IsForeground; }
            if (_gameForeground != foreground)
            {
                _gameForeground = foreground;
                PlaybackRenderDiagnostics.Reset();
                if (foreground) PlaybackRenderDiagnostics.BeginFocusRecovery();

            }
            // Poll once per second, including when no TV is visible. Another
            // renderer can be the first to notice a shared-device failure.
            var now = Environment.TickCount64;
            if (!_deviceFailure.Failed && now >= _nextDeviceCheck)
            {
                _nextDeviceCheck = now + 1000;
                CheckDeviceFailure();
            }
            using (TvUiRenderer.EnterContext())
            {
            if (Volatile.Read(ref _resizeDepth) == 0)
            {
			foreach (var pending in _pendingRenderWork)
			{
                if (_shuttingDown) break;
				if (_pendingRenderWork.TryRemove(pending.Key, out Action? work))
				{
					try
					{
						work();
					}
					catch (Exception e)
					{
                        if (GraphicsDeviceFailure.IsDeviceError(e.HResult))
                            ReportDeviceFailure(e.HResult, GraphicsDeviceFailure.Source.RenderThreadCallback);
                        else AepLog.Error("[DxHandler] Render-thread callback '[redacted]' failed.");
					}
				}
			}

			if (!_shuttingDown && !_deviceFailure.Failed)
			{
				try
				{
					OnPresent?.Invoke();
				}
				catch (Exception e)
				{
                    if (GraphicsDeviceFailure.IsDeviceError(e.HResult))
                        ReportDeviceFailure(e.HResult, GraphicsDeviceFailure.Source.OnPresentSubscriber);
                    else AepLog.Error("[DxHandler] OnPresent subscriber failed.");
				}
                finally { TvUiRenderer.EndFrame(); }
			}
            }
            }

            var originalStarted = System.Diagnostics.Stopwatch.GetTimestamp();
            int result;
            try { result = original(swapChain, syncInterval, flags); }
            catch (Exception exception) when (GraphicsDeviceFailure.IsDeviceError(exception.HResult))
            {
                // A managed DXGI exception from a downstream hook must not
                // unwind through the native game's Present call. Preserve the
                // actual failing HRESULT for the game's normal error handling.
                result = exception.HResult;
                ReportDeviceFailure(result, GraphicsDeviceFailure.Source.OriginalPresentHookChain);
            }
            if (GraphicsDeviceFailure.IsDeviceError(result))
                ReportDeviceFailure(result, GraphicsDeviceFailure.Source.PresentResult);
            if (!_shuttingDown)
                PlaybackRenderDiagnostics.RecordPresent(
                    System.Diagnostics.Stopwatch.GetElapsedTime(started, originalStarted).TotalMilliseconds,
                    System.Diagnostics.Stopwatch.GetElapsedTime(originalStarted).TotalMilliseconds);
            return result;
		}
		finally
		{
            if (beganPresent) TvUiRenderer.EndPresent();
			lock (_presentLifetimeLock)
			{
				if (--_activePresentCallbacks == 0)
				{
					_presentIdle.Set();
				}
			}
		}
	}

    private static void CheckDeviceFailure()
    {
        try
        {
            var reason = Device?.DeviceRemovedReason.Code ?? 0;
            if (reason < 0) ReportDeviceFailure(reason, GraphicsDeviceFailure.Source.HealthCheck);
        }
        catch (Exception exception) when (GraphicsDeviceFailure.IsDeviceError(exception.HResult))
        { ReportDeviceFailure(exception.HResult, GraphicsDeviceFailure.Source.HealthCheck); }
    }

    internal static void ReportDeviceFailure(int result, GraphicsDeviceFailure.Source source)
    {
        if (!_deviceFailure.TryMark(result)) return;
        int? reason = null;
        try { reason = Device?.DeviceRemovedReason.Code; }
        catch (Exception) { /* Reporting must not replace the original graphics failure. */ }
        AepLog.GraphicsFailure(source, result, reason);
        TvUiRenderer.OnDeviceFailure();
    }

	public static void Dispose()
	{
        if (Interlocked.Exchange(ref _disposeStarted, 1) != 0) return;
        BeginShutdown();
        _pendingRenderWork.Clear();
        OnPresent = null;

        // Close native admission before touching the pointer hooks. Admitted
        // calls own a cached original and can forward even after Dalamud
        // disposes its hook wrappers.
        _presentRelay?.Bypass();
        _resizeRelay?.Bypass();
        _presentHook?.Disable();
        _resizeHook?.Disable();

        if (Device is null && _presentHook is null && _resizeHook is null && _shutdownRenderWork.IsEmpty)
        {
            ReleaseGraphicsLease();
            return;
        }
        var cleanup = Task.Run(async () =>
        {
            try
            {
                // The native reservation includes the transition into and out
                // of managed code. Keep both relays rooted until it is idle.
                while (_presentRelay?.IsDrained == false || _resizeRelay?.IsDrained == false || !_presentIdle.IsSet)
                    await Task.Delay(10).ConfigureAwait(false);
                await FinishShutdownAsync().ConfigureAwait(false);
            }
            catch (Exception) { AepLog.Warning("[DxHandler] Deferred graphics cleanup failed."); }
        });
        if (!_presentIdle.IsSet && Environment.CurrentManagedThreadId == Volatile.Read(ref _renderThreadId)) return;
        if (!cleanup.Wait(TimeSpan.FromSeconds(2)))
            AepLog.Warning("[DxHandler] Graphics cleanup is still waiting for the renderer.");
	}

    private static async Task FinishShutdownAsync()
    {
        _presentHook?.Dispose();
        _resizeHook?.Dispose();
        _presentHook = null;
        _resizeHook = null;
        _presentOriginal = null;
        _resizeOriginal = null;
        _presentRelay = null;
        _resizeRelay = null;
        _swapChainPointer = 0;
        _renderThreadId = 0;

        if (Device is not null) await TvUiRenderer.ShutdownAsync().ConfigureAwait(false);
        while (_shutdownRenderWork.TryDequeue(out var release))
        {
            try { release(); }
            catch (Exception) { AepLog.Warning("[DxHandler] A graphics resource could not be released."); }
        }
        if (Device is { } device)
        {
            TvUiRenderer.ReleaseContext();
            device.Dispose();
            Device = null;
        }
        ReleaseGraphicsLease();
    }

    private static void ReleaseGraphicsLease()
    {
        using var lease = Interlocked.Exchange(ref _graphicsLease, null);
        lease?.Release();
    }
}

using Dalamud.Game.ClientState.GamePad;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.System.Input;

namespace AlphaChannel.Plugin.Video;

// Snapshot the freshly polled native pad data before suppressing FFXIV input.
// Dalamud's cached state can be updated before OR after this hook; do not read it here.
// No keyboard state or global ImGui navigation flags are changed from this native callback.
internal sealed unsafe class RetroGamepadCapture : IDisposable
{
    private readonly object gate = new();
    private readonly Func<bool> wantsCapture;
    private readonly RetroControllerRouting routing = new();
    private readonly Hook<PadDevice.Delegates.Update> hook;
    private int callbacks, capturedButtons;
    private bool disposed, failed;
    internal bool Failed => Volatile.Read(ref failed);
    internal bool Blocking { get { lock(gate) return !disposed && !Failed && routing.Blocking; } }

    internal RetroGamepadCapture(Func<bool> wantsCapture)
    {
        this.wantsCapture = wantsCapture;
        if (PadDevice.StaticVirtualTablePointer == null || PadDevice.StaticVirtualTablePointer->Update == null)
            throw new InvalidOperationException("Controller input is unavailable.");
        hook = Plugin.InteropProvider.HookFromAddress<PadDevice.Delegates.Update>(
            (nint)PadDevice.StaticVirtualTablePointer->Update, Poll);
        try { hook.Enable(); }
        catch { hook.Dispose(); throw; }
    }

    internal bool Held(GamepadButtons button) => (Volatile.Read(ref capturedButtons) & (int)button) != 0;

    internal void ResetInput()
    {
        lock(gate) { routing.Reset(); Volatile.Write(ref capturedButtons, 0); }
    }

    private void Poll(PadDevice* device)
    {
        Interlocked.Increment(ref callbacks);
        try
        {
            hook.Original(device);
            lock(gate)
            {
                if (disposed || Failed || device == null) return;
                var capture = wantsCapture() && device->ActiveGamepadDeviceIndex >= 0;
                if (!capture && !routing.Blocking) { Volatile.Write(ref capturedButtons, 0); return; }
                var raw = (int)device->GamepadInputData.Buttons;
                // Retro games use digital buttons, not analogue sticks. Resting stick drift
                // must not keep entry locked. On release wait for sticks to return to their deadzone.
                var neutral = raw == 0 && (capture ||
                    Math.Abs((long)device->GamepadInputData.LeftStickX) <= 20 &&
                    Math.Abs((long)device->GamepadInputData.LeftStickY) <= 20 &&
                    Math.Abs((long)device->GamepadInputData.RightStickX) <= 20 &&
                    Math.Abs((long)device->GamepadInputData.RightStickY) <= 20);
                var decision = routing.Poll(capture, neutral);
                Volatile.Write(ref capturedButtons, decision.Read ? raw : 0);
                if (!decision.Block) return;
                device->GamepadInputData.Buttons = 0;
                device->GamepadInputData.ButtonsPressed = 0;
                device->GamepadInputData.ButtonsReleased = 0;
                device->GamepadInputData.ButtonsRepeat = 0;
                device->GamepadInputData.LeftStickX = device->GamepadInputData.LeftStickY = 0;
                device->GamepadInputData.RightStickX = device->GamepadInputData.RightStickY = 0;
            }
        }
        catch (Exception ex)
        {
            Volatile.Write(ref failed, true); Volatile.Write(ref capturedButtons, 0);
            AepLog.Warning("[Retro Games] Controller capture stopped: " + ex.GetType().Name);
        }
        finally { Interlocked.Decrement(ref callbacks); }
    }

    public void Dispose()
    {
        lock(gate)
        {
            if (disposed) return;
            disposed = true; Volatile.Write(ref capturedButtons, 0); hook.Disable();
        }
        // A callback waiting for gate still needs the original trampoline.
        _ = Task.Run(() =>
        {
            SpinWait.SpinUntil(() => Volatile.Read(ref callbacks) == 0);
            try { hook.Dispose(); }
            catch (Exception ex) { AepLog.Warning("[Retro Games] Controller hook cleanup failed: " + ex.GetType().Name); }
            GC.KeepAlive(this);
        });
    }
}

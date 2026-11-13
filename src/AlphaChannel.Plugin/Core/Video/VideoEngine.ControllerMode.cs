using Dalamud.Game.ClientState.GamePad;
using Dalamud.Game.ClientState.Keys;

namespace AlphaChannel.Plugin.Video;

internal sealed partial class VideoEngine
{
    private RetroGamepadCapture? retroGamepadCapture;
    private bool controllerModeEnabled, restoreControllerNavigation, controllerHadFocus;
    internal bool ControllerModeEnabled => Volatile.Read(ref controllerModeEnabled);

    internal void SetRetroControlMode(RetroControlMode mode)
    {
        SetSnesControlsEnabled(false);
        SetGameBoyControlsEnabled(false);
        if (mode == RetroControlMode.Ffxiv || !IsPlayingGame) return;
        if (mode == RetroControlMode.Controller)
        {
            try
            {
                retroGamepadCapture ??= new(() => ControllerModeEnabled && IsPlayingGame &&
                    (_isPlayingSnes ? SnesControlsEnabled : GameBoyControlsEnabled) &&
                    !_disposing && GameWindowFocus.IsForeground && Plugin.ClientState.IsLoggedIn);
                if (retroGamepadCapture.Failed) throw new InvalidOperationException("Controller capture failed.");
                var navigationEnabled = Plugin.GamepadState.EnableGamepadNav;
                restoreControllerNavigation |= navigationEnabled;
                if (navigationEnabled) Plugin.GamepadState.EnableGamepadNav = false;
                retroGamepadCapture.ResetInput();
            }
            catch (Exception ex)
            {
                ReleaseControllerMode();
                AepLog.Warning("[Retro Games] Controller Mode unavailable: " + ex.GetType().Name);
                Plugin.ChatGui.Print("[AlphaChannel] Controller Mode is unavailable. Control remains with FFXIV.");
                return;
            }
        }
        if (_isPlayingSnes) SetSnesControlsEnabled(true);
        else SetGameBoyControlsEnabled(true);
        if (mode == RetroControlMode.Controller)
        {
            controllerHadFocus = GameWindowFocus.IsForeground;
            Volatile.Write(ref controllerModeEnabled, true);
        }
    }

    private void ReleaseControllerMode()
    {
        var wasController = ControllerModeEnabled;
        Volatile.Write(ref controllerModeEnabled, false);
        if (wasController) retroGamepadCapture?.ResetInput();
        controllerHadFocus = false;
        RestoreControllerNavigation();
    }

    private void RestoreControllerNavigation(bool unloading = false)
    {
        if (!restoreControllerNavigation || (!unloading && retroGamepadCapture?.Blocking == true)) return;
        restoreControllerNavigation = false;
        if (!Plugin.GamepadState.EnableGamepadNav) Plugin.GamepadState.EnableGamepadNav = true;
    }

    private void UpdateControllerMode()
    {
        if (!ControllerModeEnabled) { RestoreControllerNavigation(); return; }
        var foreground = GameWindowFocus.IsForeground;
        if (!foreground && controllerHadFocus) retroGamepadCapture?.ResetInput();
        controllerHadFocus = foreground;
        if (!IsPlayingGame || !Plugin.ClientState.IsLoggedIn || retroGamepadCapture?.Failed == true || Plugin.GamepadState.EnableGamepadNav)
            SetRetroControlMode(RetroControlMode.Ffxiv);
    }

    private bool RetroKeyHeld(VirtualKey key) => !ControllerModeEnabled && IsSnesKeyHeld(key);
    private bool RetroPadHeld(GamepadButtons button) => ControllerModeEnabled
        ? retroGamepadCapture?.Held(button) == true : Plugin.GamepadState.Raw(button) != 0;
    private void DisposeControllerMode()
    {
        try { ReleaseControllerMode(); }
        finally
        {
            retroGamepadCapture?.Dispose();
            RestoreControllerNavigation(unloading: true);
        }
    }
}

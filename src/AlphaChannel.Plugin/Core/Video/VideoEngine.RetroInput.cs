using Dalamud.Game.ClientState.GamePad;
using Dalamud.Game.ClientState.Keys;

namespace AlphaChannel.Plugin.Video;

internal sealed partial class VideoEngine
{
    private bool TryForceFfxivControl()
    {
        var chordDown =
            IsSnesKeyHeld(
                VirtualKey.CONTROL) &&
            IsSnesKeyHeld(
                VirtualKey.F12);


        //
        // Trigger only once per press instead of once every frame
        // while Ctrl + F12 is being held.
        //

        var triggered =
            chordDown &&
            !_forceFfxivResetChordWasDown;

        _forceFfxivResetChordWasDown =
            chordDown;


        if (!triggered)
        {
            return false;
        }


        if (_isPlayingSnes)
        {
            SetSnesControlsEnabled(
                false);
        }

        if (_isPlayingGameBoy || _isPlayingNes || _isPlayingGameBoyAdvance || _isPlayingMasterSystem || _isPlayingGameGear)
        {
            SetGameBoyControlsEnabled(
                false);
        }

        if (_isPlayingBrowser)
        {
            SetBrowserControlsEnabled(false);
        }


        Plugin.ChatGui.Print(
            "[AlphaChannel] Media controls released. Control returned to FFXIV.");


        return true;
    }


    private void UpdateSnesInput()
    {
        if (!_isPlayingSnes || _snesRenderer is null)
        {
            return;
        }

        if (!GameWindowFocus.IsForeground)
        {
            _forceFfxivResetChordWasDown = false;
            foreach (Snes9xInput input in Enum.GetValues<Snes9xInput>())
                _snesRenderer.SetButton(0, (int)input, false);
            return;
        }

        if (!SnesControlsEnabled)
        {
            _forceFfxivResetChordWasDown =
                false;

            return;
        }


        //
        // Ctrl + F12 always wins over emulator ownership.
        //

        if (TryForceFfxivControl())
        {
            return;
        }


        bool Pad(GamepadButtons button) =>
                    RetroPadHeld(button);

        //
        // Read keyboard directly from Windows.
        //
        // This is deliberately separate from Dalamud KeyState because
        // KeyState is cleared below so FFXIV does not react to SNES keys.
        //

        var cfg =
    Plugin.Cfg;

        var keyUp =
            (VirtualKey)cfg.SnesKeyUp;

        var keyDown =
            (VirtualKey)cfg.SnesKeyDown;

        var keyLeft =
            (VirtualKey)cfg.SnesKeyLeft;

        var keyRight =
            (VirtualKey)cfg.SnesKeyRight;

        var keyA =
            (VirtualKey)cfg.SnesKeyA;

        var keyB =
            (VirtualKey)cfg.SnesKeyB;

        var keyX =
            (VirtualKey)cfg.SnesKeyX;

        var keyY =
            (VirtualKey)cfg.SnesKeyY;

        var keyL =
            (VirtualKey)cfg.SnesKeyL;

        var keyR =
            (VirtualKey)cfg.SnesKeyR;

        var keyStart =
            (VirtualKey)cfg.SnesKeyStart;

        var keySelect =
            (VirtualKey)cfg.SnesKeySelect;


        _snesRenderer.SetButton(
            0,
            (int)Snes9xInput.UP,
            RetroKeyHeld(keyUp) ||
            Pad((GamepadButtons)cfg.GamepadUp));

        _snesRenderer.SetButton(
            0,
            (int)Snes9xInput.DOWN,
            RetroKeyHeld(keyDown) ||
            Pad((GamepadButtons)cfg.GamepadDown));

        _snesRenderer.SetButton(
            0,
            (int)Snes9xInput.LEFT,
            RetroKeyHeld(keyLeft) ||
            Pad((GamepadButtons)cfg.GamepadLeft));

        _snesRenderer.SetButton(
            0,
            (int)Snes9xInput.RIGHT,
            RetroKeyHeld(keyRight) ||
            Pad((GamepadButtons)cfg.GamepadRight));

        _snesRenderer.SetButton(
            0,
            (int)Snes9xInput.B,
            RetroKeyHeld(keyB) ||
            Pad((GamepadButtons)cfg.GamepadB));

        _snesRenderer.SetButton(
            0,
            (int)Snes9xInput.A,
            RetroKeyHeld(keyA) ||
            Pad((GamepadButtons)cfg.GamepadA));

        _snesRenderer.SetButton(
            0,
            (int)Snes9xInput.Y,
            RetroKeyHeld(keyY) ||
            Pad((GamepadButtons)cfg.GamepadY));

        _snesRenderer.SetButton(
            0,
            (int)Snes9xInput.X,
            RetroKeyHeld(keyX) ||
            Pad((GamepadButtons)cfg.GamepadX));

        _snesRenderer.SetButton(
            0,
            (int)Snes9xInput.L,
            RetroKeyHeld(keyL) ||
            Pad((GamepadButtons)cfg.GamepadL));

        _snesRenderer.SetButton(
            0,
            (int)Snes9xInput.R,
            RetroKeyHeld(keyR) ||
            Pad((GamepadButtons)cfg.GamepadR));

        _snesRenderer.SetButton(
            0,
            (int)Snes9xInput.START,
            RetroKeyHeld(keyStart) ||
            Pad((GamepadButtons)cfg.GamepadStart));

        _snesRenderer.SetButton(
            0,
            (int)Snes9xInput.SELECT,
            RetroKeyHeld(keySelect) ||
            Pad((GamepadButtons)cfg.GamepadSelect));


        //
        // Remove those keyboard presses from Dalamud's game-facing
        // key state so FFXIV does not react to them.
        //

        if (ControllerModeEnabled) return;

        VirtualKey[] snesKeys =
 [
     keyUp,
    keyDown,
    keyLeft,
    keyRight,

    keyA,
    keyB,
    keyX,
    keyY,

    keyL,
    keyR,

    keyStart,
    keySelect
 ];


        if (BlockAllFfxivKeyboardInput)
        {
            SuppressAllFfxivKeyboardInput();
        }
        else
        {
            //
            // Normal mode:
            // only consume keys actually assigned to SNES.
            //

            foreach (var key in
                     snesKeys)
            {
                if (Plugin.KeyState[key])
                {
                    Plugin.KeyState[key] =
                        false;
                }
            }
        }
    }

    private void UpdateGameBoyInput()
    {
        var renderer = _isPlayingNes ? _nesRenderer : _isPlayingGameBoyAdvance ? _gameBoyAdvanceRenderer : _isPlayingMasterSystem ? _masterSystemRenderer : _isPlayingGameGear ? _gameGearRenderer : _gameBoyRenderer;
        if ((!_isPlayingGameBoy && !_isPlayingNes && !_isPlayingGameBoyAdvance && !_isPlayingMasterSystem && !_isPlayingGameGear) || renderer is null)
        {
            return;
        }

        if (!GameWindowFocus.IsForeground)
        {
            _forceFfxivResetChordWasDown = false;
            foreach (GambatteInput input in Enum.GetValues<GambatteInput>())
                renderer.SetButton((int)input, false);
            return;
        }

        if (!GameBoyControlsEnabled)
        {
            _forceFfxivResetChordWasDown =
                false;

            return;
        }


        //
        // Ctrl + F12 always returns keyboard ownership to FFXIV.
        //

        if (TryForceFfxivControl())
        {
            return;
        }


        bool Pad(
                    GamepadButtons button) =>
            RetroPadHeld(button);


        //
        // For the first Game Boy pass, reuse the matching
        // SNES keyboard bindings.
        //
        // Game Boy only needs:
        // D-pad, A, B, Start and Select.
        //

        var cfg =
            Plugin.Cfg;

        var keyUp =
            (VirtualKey)cfg.SnesKeyUp;

        var keyDown =
            (VirtualKey)cfg.SnesKeyDown;

        var keyLeft =
            (VirtualKey)cfg.SnesKeyLeft;

        var keyRight =
            (VirtualKey)cfg.SnesKeyRight;

        var keyA =
            (VirtualKey)cfg.SnesKeyA;

        var keyB =
            (VirtualKey)cfg.SnesKeyB;

        var keyL =
            (VirtualKey)cfg.SnesKeyL;

        var keyR =
            (VirtualKey)cfg.SnesKeyR;

        var keyStart =
            (VirtualKey)cfg.SnesKeyStart;

        var keySelect =
            (VirtualKey)cfg.SnesKeySelect;


        //
        // D-pad
        //

        renderer.SetButton(
            (int)GambatteInput.Up,
            RetroKeyHeld(
                keyUp) ||
            Pad(
                (GamepadButtons)cfg.GamepadUp));

        renderer.SetButton(
            (int)GambatteInput.Down,
            RetroKeyHeld(
                keyDown) ||
            Pad(
                (GamepadButtons)cfg.GamepadDown));

        renderer.SetButton(
            (int)GambatteInput.Left,
            RetroKeyHeld(
                keyLeft) ||
            Pad(
                (GamepadButtons)cfg.GamepadLeft));

        renderer.SetButton(
            (int)GambatteInput.Right,
            RetroKeyHeld(
                keyRight) ||
            Pad(
                (GamepadButtons)cfg.GamepadRight));


        //
        // Face buttons
        //

        renderer.SetButton(
            (int)GambatteInput.B,
            RetroKeyHeld(
                keyB) ||
            Pad(
                (GamepadButtons)cfg.GamepadB));

        renderer.SetButton(
            (int)GambatteInput.A,
            RetroKeyHeld(
                keyA) ||
            Pad(
                (GamepadButtons)cfg.GamepadA));

        if (_isPlayingGameBoyAdvance)
        {
            renderer.SetButton((int)GambatteInput.L,
                RetroKeyHeld(keyL) || Pad((GamepadButtons)cfg.GamepadL));
            renderer.SetButton((int)GambatteInput.R,
                RetroKeyHeld(keyR) || Pad((GamepadButtons)cfg.GamepadR));
        }


        //
        // Start / Select
        //

        renderer.SetButton(
            (int)GambatteInput.Start,
            RetroKeyHeld(
                keyStart) ||
            Pad(
                (GamepadButtons)cfg.GamepadStart));

        renderer.SetButton(
            (int)GambatteInput.Select,
            RetroKeyHeld(
                keySelect) ||
            Pad(
                (GamepadButtons)cfg.GamepadSelect));


        //
        // Prevent FFXIV from receiving the Game Boy
        // keyboard controls while emulator input is active.
        //

        if (ControllerModeEnabled) return;

        var gameBoyKeys = new List<VirtualKey>
 {
     keyUp,
    keyDown,
    keyLeft,
    keyRight,
    keyA,
    keyB,
    keyStart,
    keySelect
 };

        if (_isPlayingGameBoyAdvance)
        {
            gameBoyKeys.Add(keyL);
            gameBoyKeys.Add(keyR);
        }


        if (BlockAllFfxivKeyboardInput)
        {
            SuppressAllFfxivKeyboardInput();
        }
        else
        {
            //
            // Normal mode:
            // only consume Game Boy-bound keyboard keys.
            //

            foreach (var key in
                     gameBoyKeys)
            {
                if (Plugin.KeyState[key])
                {
                    Plugin.KeyState[key] =
                        false;
                }
            }
        }
    }

}

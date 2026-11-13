using Dalamud.Game.ClientState.GamePad;
using Dalamud.Game.ClientState.Keys;

namespace AlphaChannel.Plugin.Video;

internal sealed partial class VideoEngine
{
    internal void SetSnesControlsEnabled(bool enabled)
    {
        if (!enabled) ReleaseControllerMode();
        if (SnesControlsEnabled == enabled)
        {
            return;
        }

        SnesControlsEnabled = enabled;

        // Make absolutely sure no SNES button remains "held"
        // when control is handed back to FFXIV.
        if (!enabled && _snesRenderer is not null)
        {
            foreach (Snes9xInput input in Enum.GetValues<Snes9xInput>())
            {
                _snesRenderer.SetButton(
                    0,
                    (int)input,
                    false);
            }
        }
    }

    internal void SetGameBoyControlsEnabled(
    bool enabled)
    {
        if (!enabled) ReleaseControllerMode();
        if (GameBoyControlsEnabled ==
            enabled)
        {
            return;
        }

        GameBoyControlsEnabled =
            enabled;

        //
        // Release every Game Boy button when input is
        // handed back to FFXIV.
        //

        if (!enabled &&
            (_gameBoyRenderer is not null || _nesRenderer is not null || _gameBoyAdvanceRenderer is not null || _masterSystemRenderer is not null || _gameGearRenderer is not null))
        {
            foreach (GambatteInput input in
                     Enum.GetValues<GambatteInput>())
            {
                (_isPlayingNes ? _nesRenderer : _isPlayingGameBoyAdvance ? _gameBoyAdvanceRenderer : _isPlayingMasterSystem ? _masterSystemRenderer : _isPlayingGameGear ? _gameGearRenderer : _gameBoyRenderer)?.SetButton(
                    (int)input,
                    false);
            }
        }
    }

}

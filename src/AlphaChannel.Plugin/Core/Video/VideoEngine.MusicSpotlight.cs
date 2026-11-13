using System.Numerics;
using AlphaChannel.Plugin.Lighting;

namespace AlphaChannel.Plugin.Video;

internal sealed partial class VideoEngine
{
    private NativePointLight? musicSpotlight;
    private NativeLightPose? musicSpotlightPose;

    private void UpdateMusicSpotlight(bool playbackActive)
    {
        if (_screenPainter is not { } painter) return;
        var scene = painter.MusicEffects;
        if (!playbackActive || !IsActive || IsShowingScreensaver || !HasWorldPlacement || !painter.MusicSceneActive ||
            (scene.EffectiveMask & MusicSceneEffects.Spotlight) == 0 || scene.SpotlightBrightness <= .0001f)
        {
            DisposeMusicSpotlight();
            return;
        }
        // Match the native show lights' colour strength. Brightness is the
        // separate intensity multiplier, not a change to global lighting.
        var rgb = scene.SpotlightColour;
        var colour = new Vector3((rgb >> 16) & 255, (rgb >> 8) & 255, rgb & 255) * (20f / 255f);
        if (musicSpotlightPose is not { } old || old.Position != ScreenPosition || old.Color != colour ||
            old.Intensity != scene.SpotlightBrightness || old.Range != scene.SpotlightRange)
            musicSpotlightPose = new(ScreenPosition, scene.SpotlightBrightness, scene.SpotlightRange, colour);
        // Driven by this existing framework tick: no extra subscription and no
        // repeating native updates/pose allocations for an unchanged light.
        musicSpotlight ??= new NativePointLight("Music spotlight", autoUpdate: false);
        musicSpotlight.SetDesired(musicSpotlightPose);
        musicSpotlight.UpdateNow();
    }

    private void DisposeMusicSpotlight()
    {
        var light = musicSpotlight;
        musicSpotlight = null; musicSpotlightPose = null;
        try { light?.Dispose(); }
        catch { AepLog.Warning("[Music] Spotlight cleanup failed."); }
    }
}

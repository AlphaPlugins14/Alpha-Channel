using System.Numerics;

namespace AlphaChannel.Plugin.Lighting;

// Only identified RGB fields. Fog opacity, cloud placement and all unknown fields stay untouched.
internal readonly record struct SceneAtmosphereValues(Vector3 Fog,Vector3 CloudA,Vector3 CloudB,Vector3 Moon)
{
    internal bool Valid => SceneLightingValues.ValidColor(Fog) && SceneLightingValues.ValidColor(CloudA) &&
        SceneLightingValues.ValidColor(CloudB) && SceneLightingValues.ValidColor(Moon);
    internal SceneAtmosphereValues Scaled(float level)
    {
        level=float.IsFinite(level)?Math.Clamp(level,0,1):1;
        return new(Fog*level,CloudA*level,CloudB*level,Moon*level);
    }
}

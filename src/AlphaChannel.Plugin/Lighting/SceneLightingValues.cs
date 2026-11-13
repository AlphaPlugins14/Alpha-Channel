using System.Numerics;
using System.Runtime.InteropServices;

namespace AlphaChannel.Plugin.Lighting;

// Native environment lighting block, independently scoped to this removable test.
[StructLayout(LayoutKind.Explicit, Size = 0x40)]
internal struct SceneLightingValues
{
    [FieldOffset(0)] internal Vector3 Sun;
    [FieldOffset(0x0c)] internal Vector3 Moon;
    [FieldOffset(0x18)] internal Vector3 Ambient;
    // The remaining native fields are deliberately neither read nor changed.
    internal readonly bool Valid => ValidColor(Sun) && ValidColor(Moon) && ValidColor(Ambient);
    internal static bool ValidColor(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z) &&
        v.X >= 0 && v.Y >= 0 && v.Z >= 0 && v.X <= 100 && v.Y <= 100 && v.Z <= 100;
    internal readonly SceneLightingValues Scaled(float level)
    {
        var scaled = this;
        level = float.IsFinite(level) ? Math.Clamp(level,0,1) : 1;
        scaled.Sun *= level; scaled.Moon *= level; scaled.Ambient *= level;
        return scaled;
    }
    internal readonly bool SameColors(SceneLightingValues other) =>
        Sun == other.Sun && Moon == other.Moon && Ambient == other.Ambient;
}

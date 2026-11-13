using System.Numerics;
namespace AlphaChannel.Plugin.Lighting;
internal sealed record NativeLightPose(Vector3 Position, float Intensity, float Range, Vector3? Color = null);

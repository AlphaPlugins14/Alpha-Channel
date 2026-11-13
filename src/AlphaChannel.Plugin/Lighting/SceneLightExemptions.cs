namespace AlphaChannel.Plugin.Lighting;

// Match both objects: recycled scene or render addresses must not inherit an exemption.
internal sealed class SceneLightExemptions
{
    private readonly HashSet<(nint Scene, nint Render)> lights = [];
    internal void Add(nint scene, nint render) { if (scene != 0 && render != 0) lights.Add((scene, render)); }
    internal void Remove(nint scene, nint render) => lights.Remove((scene, render));
    internal bool Contains(nint scene, nint render) => lights.Contains((scene, render));
}

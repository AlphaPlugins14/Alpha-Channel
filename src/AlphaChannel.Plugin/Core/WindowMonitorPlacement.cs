using System.Numerics;

namespace AlphaChannel.Plugin.Core;

internal readonly record struct MonitorWorkArea(Vector2 Origin,Vector2 Size)
{
    internal Vector2 Clamp(Vector2 position,Vector2 size)=>Vector2.Clamp(position,Origin,Vector2.Max(Origin,Origin+Size-size));
}

internal static class WindowMonitorPlacement
{
    // Prefer the monitor containing most of the window. If a monitor was
    // removed, choose the nearest remaining work area rather than losing it.
    internal static MonitorWorkArea Select(Vector2 position,Vector2 size,ReadOnlySpan<MonitorWorkArea> monitors)
    {
        if(monitors.IsEmpty)throw new ArgumentException("At least one monitor is required.");
        var selected=monitors[0];float overlap=-1,distance=float.PositiveInfinity;
        foreach(var monitor in monitors)
        {
            var intersection=Vector2.Max(Vector2.Zero,Vector2.Min(position+size,monitor.Origin+monitor.Size)-Vector2.Max(position,monitor.Origin));
            var area=intersection.X*intersection.Y;
            var delta=Vector2.DistanceSquared(position,monitor.Clamp(position,size));
            if(area>overlap||area==overlap&&delta<distance){selected=monitor;overlap=area;distance=delta;}
        }
        return selected;
    }
}

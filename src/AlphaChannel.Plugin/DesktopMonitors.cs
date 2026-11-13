using System.Numerics;
using AlphaChannel.Plugin.Core;
using Dalamud.Bindings.ImGui;

namespace AlphaChannel.Plugin;

internal static class DesktopMonitors
{
    internal static MonitorWorkArea ForWindow(Vector2 position,Vector2 size)
    {
        var main=ImGui.GetMainViewport();
        var fallback=new MonitorWorkArea(main.WorkPos,main.WorkSize);
        if((ImGui.GetIO().ConfigFlags&ImGuiConfigFlags.ViewportsEnable)==0)return fallback;
        var monitors=ImGui.GetPlatformIO().Monitors;
        Span<MonitorWorkArea> areas=stackalloc MonitorWorkArea[Math.Min(monitors.Size,32)];
        var count=0;
        for(var i=0;i<areas.Length;i++)
        {
            var monitor=monitors[i];
            if(monitor.WorkSize.X>0&&monitor.WorkSize.Y>0)areas[count++]=new(monitor.WorkPos,monitor.WorkSize);
        }
        return count==0?fallback:WindowMonitorPlacement.Select(position,size,areas[..count]);
    }
}

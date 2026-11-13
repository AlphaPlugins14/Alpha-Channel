using System.Runtime.InteropServices;
using System.Numerics;

namespace AlphaChannel.Plugin.Video;

internal static class GameWindowFocus
{
    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint window, out uint processId);
    [StructLayout(LayoutKind.Sequential)]
    private struct Point { internal int X, Y; }
    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { internal int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")]
    private static extern nint WindowFromPoint(Point point);
    [DllImport("user32.dll")]
    private static extern bool ScreenToClient(nint window, ref Point point);
    [DllImport("user32.dll")]
    private static extern bool GetClientRect(nint window, out Rect rect);
    [DllImport("user32.dll")]
    private static extern nint GetAncestor(nint window, uint flags);

    // Detached Dalamud windows belong to the game process too. They can receive
    // UI input anywhere on the desktop, provided another application is not covering them.
    private static readonly UiPointerCapture UiPointer = new();
    internal static bool CanReceiveUiInput(bool buttonDown) =>
        UiPointer.Update(IsForeground, TryGetOwnedPointer(out _, out _), buttonDown);

    private static bool TryGetOwnedPointer(out nint target, out Point point)
    {
        target = 0;
        point = default;
        var window = GetForegroundWindow();
        var process = (uint)Environment.ProcessId;
        if (window == 0 || GetWindowThreadProcessId(window, out var foregroundProcess) == 0 ||
            foregroundProcess != process || !GetCursorPos(out point)) return false;
        target = WindowFromPoint(point);
        return target != 0 && GetWindowThreadProcessId(target, out var pointerProcess) != 0 && pointerProcess == process;
    }

    // World-space TV interactions use the main game viewport's client coordinates,
    // never the foreground detached window's coordinates.
    internal static bool TryGetMouse(nint gameWindow, out Vector2 mouse)
    {
        mouse = default;
        if (gameWindow == 0 || !TryGetOwnedPointer(out var target, out var point) ||
            GetAncestor(target, 2) != GetAncestor(gameWindow, 2) ||
            !ScreenToClient(gameWindow, ref point) ||
            !GetClientRect(gameWindow, out var rect) || point.X < rect.Left || point.Y < rect.Top ||
            point.X >= rect.Right || point.Y >= rect.Bottom) return false;
        mouse = new Vector2(point.X, point.Y);
        return true;
    }

    internal static bool IsForeground
    {
        get
        {
            var window = GetForegroundWindow();
            return window != 0 && GetWindowThreadProcessId(window, out var processId) != 0 &&
                processId == (uint)Environment.ProcessId;
        }
    }
}

using System.Diagnostics;
using System.Runtime.InteropServices;

// A short-lived guard for URI navigation only. Never simulates keys, attaches
// input threads, changes global focus policy or overrides a third application's focus.
internal sealed class SpotifyFocusRestore : IAsyncDisposable
{
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint processId);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint window);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(nint window);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(nint window);

    private readonly CancellationTokenSource stop = new();
    private readonly Dictionary<string, string> fields;
    private readonly Task monitor;
    private string result = "Spotify did not take focus during the navigation check";

    internal SpotifyFocusRestore(Dictionary<string, string> fields)
    {
        this.fields = fields;
        var original = GetForegroundWindow();
        GetWindowThreadProcessId(original, out var originalPid);
        monitor = WatchAsync(original, originalPid);
    }

    private static bool IsSpotify(uint pid)
    {
        try { using var process = Process.GetProcessById((int)pid); return process.ProcessName.Equals("Spotify", StringComparison.OrdinalIgnoreCase); }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
        catch (System.ComponentModel.Win32Exception) { return false; }
    }

    private async Task WatchAsync(nint original, uint originalPid)
    {
        if (original == 0 || originalPid == 0 || IsSpotify(originalPid))
        { result = "No separate original window to restore"; return; }
        var clock = Stopwatch.StartNew();
        try
        {
            while (clock.Elapsed < TimeSpan.FromSeconds(3))
            {
                await Task.Delay(75, stop.Token);
                GetWindowThreadProcessId(original, out var stillOriginalPid);
                if (!IsWindow(original) || IsIconic(original) || stillOriginalPid != originalPid)
                { result = "Original window closed, minimized or replaced; focus left alone"; return; }
                var current = GetForegroundWindow();
                if (current == 0 || current == original) continue;
                GetWindowThreadProcessId(current, out var currentPid);
                if (!IsSpotify(currentPid))
                { result = "Another application took focus; focus left alone"; return; }
                // Recheck immediately before the single restore attempt.
                if (GetForegroundWindow() != current) continue;
                result = SetForegroundWindow(original) && GetForegroundWindow() == original
                    ? "Restored the original window after Spotify took focus"
                    : "Windows declined focus restoration";
                return;
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
    }

    public async ValueTask DisposeAsync()
    {
        stop.Cancel();
        try { await monitor; }
        finally
        {
            fields["Automation / Focus restoration"] = result;
            stop.Dispose();
        }
    }
}

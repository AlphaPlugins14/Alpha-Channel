using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using AlphaChannel.BroadcastStudio;
using NAudio.CoreAudioApi;

namespace AlphaChannel.WebViewHost;

internal static class StudioApplications
{
    internal static StudioApplication[] List(int excludedProcessId)
    {
        var parents = Parents();
        var candidates = new HashSet<int>();
        // Include visible apps before they start playing, and background music apps
        // with an audio session. Temporary session wrappers are always released.
        foreach (var process in Process.GetProcesses())
            using (process)
                try
                {
                    // Spotify may be paused in the tray when Play is pressed,
                    // before it has a visible window or an active audio session.
                    if (process.MainWindowHandle != IntPtr.Zero ||
                        process.ProcessName.Equals("Spotify", StringComparison.OrdinalIgnoreCase))
                        candidates.Add(process.Id);
                }
                catch { }
        try
        {
            using var devices = new MMDeviceEnumerator();
            foreach (var device in devices.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
                using (device)
                {
                    var sessions = device.AudioSessionManager.Sessions;
                    for (var i = 0; i < sessions.Count; i++)
                        using (var session = sessions[i])
                            if (!session.IsSystemSoundsSession) candidates.Add((int)session.GetProcessID);
                }
        }
        catch { }
        var result = new Dictionary<int, StudioApplication>();
        foreach (var candidate in candidates)
        {
            try
            {
                var root = candidate;
                using var original = Process.GetProcessById(root);
                var name = original.ProcessName;
                var started = original.StartTime;
                // Chromium and Spotify render from child processes. Select the
                // highest same-executable ancestor so those children are included.
                var seen = new HashSet<int> { root };
                while (parents.TryGetValue(root, out var parent) && seen.Add(parent))
                {
                    try
                    {
                        using var ancestor = Process.GetProcessById(parent);
                        if (!ancestor.ProcessName.Equals(name, StringComparison.OrdinalIgnoreCase) ||
                            ancestor.StartTime > started) break;
                        root = parent; started = ancestor.StartTime;
                    }
                    catch { break; } // An exited launcher must not hide a still-running app.
                }
                if (IsExcluded(root, excludedProcessId, parents) ||
                    name.StartsWith("AlphaChannel.", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("ffmpeg", StringComparison.OrdinalIgnoreCase)) continue;
                using var app = Process.GetProcessById(root);
                var label = name;
                try { label = app.MainModule?.FileVersionInfo.FileDescription ?? name; } catch { }
                if (string.IsNullOrWhiteSpace(label)) label = name;
                result[root] = new StudioApplication(root, app.StartTime.ToUniversalTime().Ticks, label)
                    { ProcessName = name };
            }
            catch { /* App exited or is inaccessible. */ }
        }
        return result.Values.OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase).ThenBy(a => a.ProcessId).ToArray();
    }
    internal static bool IsExcluded(int id, int excluded, Dictionary<int, int>? parents = null)
    {
        if (id == excluded) return true;
        parents ??= Parents();
        var target = id;
        var seen = new HashSet<int>();
        while (id > 0 && seen.Add(id))
        {
            // The helper's own children carry our decoder/monitor audio.
            // Apps launched by FFXIV can be captured independently: including
            // their subtree does not include their parent game's audio.
            if (id == Environment.ProcessId) return true;
            if (!parents.TryGetValue(id, out id)) break;
        }
        // Capturing an ancestor would include the plugin's output too.
        foreach (var own in new[] { excluded, Environment.ProcessId })
        {
            seen.Clear(); id = own;
            while (id > 0 && seen.Add(id))
            {
                if (id == target) return true;
                if (!parents.TryGetValue(id, out id)) break;
            }
        }
        return false;
    }
    private static Dictionary<int, int> Parents()
    {
        var result = new Dictionary<int, int>();
        using var snapshot = CreateToolhelp32Snapshot(2, 0);
        if (snapshot.IsInvalid) return result;
        var entry = new ProcessEntry { Size = (uint)Marshal.SizeOf<ProcessEntry>() };
        if (Process32First(snapshot, ref entry))
            do { result[(int)entry.ProcessId] = (int)entry.ParentProcessId; } while (Process32Next(snapshot, ref entry));
        return result;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry
    {
        public uint Size, Usage, ProcessId;
        public UIntPtr DefaultHeap;
        public uint ModuleId, Threads, ParentProcessId;
        public int BasePriority;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string ExeFile;
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeFileHandle CreateToolhelp32Snapshot(uint flags, uint processId);
    [DllImport("kernel32.dll", EntryPoint = "Process32FirstW", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool Process32First(SafeFileHandle snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll", EntryPoint = "Process32NextW", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool Process32Next(SafeFileHandle snapshot, ref ProcessEntry entry);
}

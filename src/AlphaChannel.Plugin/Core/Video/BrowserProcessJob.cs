using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace AlphaChannel.Plugin.Video;

// Each session owns a Windows job. Its children remain owned even if the
// helper exits first; closing the handle also cleans up after plugin/game exit.
internal sealed class BrowserProcessJob : IDisposable
{
    private readonly SafeFileHandle handle;
    internal BrowserProcessJob()
    {
        handle = CreateJobObject(0, null);
        if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot create browser process job.");
        var limits = new ExtendedLimits { Basic = new BasicLimits { Flags = 0x2000 } }; // KILL_ON_JOB_CLOSE
        if (!SetInformationJobObject(handle, 9, ref limits, (uint)Marshal.SizeOf<ExtendedLimits>()))
        {
            var error = Marshal.GetLastWin32Error(); handle.Dispose();
            throw new Win32Exception(error, "Cannot configure browser process cleanup.");
        }
    }
    internal void Attach(Process process)
    {
        if (!AssignProcessToJobObject(handle, process.Handle))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot contain browser process tree.");
    }
    internal void StopAndWait()
    {
        var ids = ProcessIds();
        if (ids.Length == 0) return;
        // Job membership can reach zero before Windows has signalled that a
        // process has finished exiting and released its executable mappings.
        // Hold process handles and wait for actual exit as well.
        var processes = new List<Process>();
        var watch = Stopwatch.StartNew();
        try
        {
            foreach (var id in ids)
            {
                try
                {
                    var process = Process.GetProcessById(id);
                    try { _ = process.Handle; processes.Add(process); }
                    catch { process.Dispose(); throw; }
                }
                catch (ArgumentException) { } // Already exited.
            }
            if (!TerminateJobObject(handle, 0))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot stop browser process tree.");
            foreach (var process in processes)
                if (!process.WaitForExit(Math.Max(0, 2000 - (int)watch.ElapsedMilliseconds)))
                    throw new TimeoutException("Browser process tree did not exit within 2 seconds.");
            while (ProcessIds().Length > 0)
            {
                if (watch.Elapsed > TimeSpan.FromSeconds(2)) throw new TimeoutException("Browser process tree did not exit within 2 seconds.");
                Thread.Sleep(10);
            }
        }
        finally { foreach (var process in processes) process.Dispose(); }
    }
    internal int[] ProcessIds()
    {
        var size = 8192;
        while (size <= 1024 * 1024)
        {
            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                if (QueryInformationJobObject(handle, 3, buffer, (uint)size, out _))
                {
                    var count = Marshal.ReadInt32(buffer, 4);
                    return Enumerable.Range(0, count).Select(i => checked((int)Marshal.ReadIntPtr(buffer, 8 + i * IntPtr.Size))).ToArray();
                }
                var error = Marshal.GetLastWin32Error();
                if (error != 234) throw new Win32Exception(error, "Cannot inspect browser process tree.");
            }
            finally { Marshal.FreeHGlobal(buffer); }
            size *= 2;
        }
        throw new InvalidOperationException("Browser process tree is unexpectedly large.");
    }
    public void Dispose() => handle.Dispose();
    [StructLayout(LayoutKind.Sequential)] private struct BasicLimits
    {
        public long ProcessTime, JobTime; public uint Flags;
        public UIntPtr MinimumWorkingSet, MaximumWorkingSet; public uint ActiveProcessLimit;
        public UIntPtr Affinity; public uint PriorityClass, SchedulingClass;
    }
    [StructLayout(LayoutKind.Sequential)] private struct IoCounters
    { public ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes; }
    [StructLayout(LayoutKind.Sequential)] private struct ExtendedLimits
    { public BasicLimits Basic; public IoCounters Io; public UIntPtr ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateJobObject(nint attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(SafeFileHandle job, int type, ref ExtendedLimits data, uint length);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(SafeFileHandle job, nint process);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateJobObject(SafeFileHandle job, uint exitCode);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryInformationJobObject(SafeFileHandle job, int type, nint data, uint length, out uint returned);
}

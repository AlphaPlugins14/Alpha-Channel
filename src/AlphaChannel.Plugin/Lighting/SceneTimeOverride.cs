using Dalamud.Memory;
using System.Runtime.InteropServices;

namespace AlphaChannel.Plugin.Lighting;

internal sealed class SceneTimeOverride : IDisposable
{
    // Weatherman's environment render-time load, replacing the seconds value
    // with seconds within the day (midnight by default). The real clock and weather stay unchanged.
    // https://github.com/NightmareXIV/Weatherman/blob/master/Weatherman/Services/MemoryManager.cs
    private const string Signature = "48 89 5C 24 ?? 57 48 83 EC 30 4C 8B 15";
    private SceneTimePatchLease? lease;

    private bool scanned;
    private bool EnsureLease()
    {
        if (scanned) return lease is not null;
        scanned = true;
        try
        {
            var address = Plugin.SigScanner.ScanText(Signature) + 0x19;
            lease = new(() => MemoryHelper.ReadRaw(address, 7), bytes => WriteInstruction(address, bytes));
            return true;
        }
        catch (Exception ex)
        {
            AepLog.Warning("[Lighting] Render-time override unavailable: " + ex.GetType().Name);
            return false;
        }
    }

    internal bool Available => EnsureLease() && lease!.Available;
    internal bool Active => lease?.Active == true;
    internal bool Start(uint seconds = 0)
    {
        try { return EnsureLease() && lease!.Enable(seconds); }
        catch (Exception ex)
        {
            AepLog.Warning("[Lighting] Render-time override failed: " + ex.GetType().Name);
            return false;
        }
    }

    private static void WriteInstruction(nint address, byte[] bytes)
    {
        var previous = MemoryHelper.ChangePermission(address, bytes.Length, MemoryProtection.ExecuteReadWrite);
        try
        {
            MemoryHelper.WriteRaw(address, bytes);
            FlushInstructionCache(GetCurrentProcess(), address, (nuint)bytes.Length);
        }
        finally { MemoryHelper.ChangePermission(address, bytes.Length, previous); }
    }

    public void Dispose()
    {
        try { lease?.Dispose(); }
        catch (Exception ex) { AepLog.Warning("[Lighting] Render-time override restore failed: " + ex.GetType().Name); }
    }

    [DllImport("kernel32.dll")] private static extern nint GetCurrentProcess();
    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FlushInstructionCache(nint process, nint address, nuint size);
}

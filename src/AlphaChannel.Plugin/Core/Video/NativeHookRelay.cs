using System.ComponentModel;
using System.Runtime.InteropServices;

namespace AlphaChannel.Plugin.Video;

/// <summary>
/// A process-lifetime native forwarding address. Other graphics hooks may retain
/// this address after our plugin unloads, so it must never point into unloaded
/// managed code after shutdown or be freed with the plugin's resources.
/// </summary>
internal sealed unsafe class NativeHookRelay<T> where T : Delegate
{
    private const int StateOffset = 80;
    private const int CallbackOffset = 88;
    private const int OriginalOffset = 96;
    private const int FunctionTableOffset = 104;
    private const int UnwindInfoOffset = 116;
    private readonly nint address;
    private readonly T callback;
    internal T EntryPoint { get; }
    private ref int State => ref *(int*)(address + StateOffset);
    internal bool IsDrained => (Volatile.Read(ref State) & int.MaxValue) == 0;

    internal NativeHookRelay(nint original, T callback, int stackArguments = 0)
    {
        if (original == 0) throw new ArgumentOutOfRangeException(nameof(original));
        if (stackArguments is < 0 or > 2) throw new ArgumentOutOfRangeException(nameof(stackArguments));
        this.callback = callback;
        if (RuntimeInformation.ProcessArchitecture != Architecture.X64)
            throw new PlatformNotSupportedException("Graphics hook relays require x64.");

        address = NativeHookRelayMemory.VirtualAlloc(0, 128, 0x3000, 0x40);
        if (address == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            // Win64 shadow space plus up to two stack arguments. The high bit
            // closes admission. A single locked increment both takes a slot
            // and decides which path to take, before loading the managed target.
            ReadOnlySpan<byte> instructions =
            [
                0x48, 0x83, 0xec, 0x38,             // sub rsp, 38h
                0xf0, 0xff, 0x05, 69, 0, 0, 0,     // lock inc dword ptr [state]
                0x78, 38,                         // js bypass
                0x48, 0x8b, 0x44, 0x24, 0x60,       // copy fifth argument
                0x48, 0x89, 0x44, 0x24, 0x20,
                0x48, 0x8b, 0x44, 0x24, 0x68,       // copy sixth argument
                0x48, 0x89, 0x44, 0x24, 0x28,
                0xff, 0x15, 49, 0, 0, 0,           // call [callback]
                0xf0, 0xff, 0x0d, 34, 0, 0, 0,     // lock dec dword ptr [state]
                0x48, 0x83, 0xc4, 0x38, 0xc3,       // add rsp, 38h; ret
                0xf0, 0xff, 0x0d, 22, 0, 0, 0,     // bypass: lock dec dword ptr [state]
                0x48, 0x83, 0xc4, 0x38,             // add rsp, 38h
                0xff, 0x25, 28, 0, 0, 0,           // jmp [original]
            ];
            var code = new Span<byte>((void*)address, instructions.Length);
            instructions.CopyTo(code);
            code.Slice(13 + stackArguments * 10, (2 - stackArguments) * 10).Fill(0x90);
            State = int.MinValue;
            *(nint*)(address + CallbackOffset) = Marshal.GetFunctionPointerForDelegate(callback);
            *(nint*)(address + OriginalOffset) = original;

            // One RUNTIME_FUNCTION and its UNWIND_INFO (UWOP_ALLOC_SMALL, 56 bytes).
            var function = (uint*)(address + FunctionTableOffset);
            function[0] = 0;
            function[1] = (uint)code.Length;
            function[2] = UnwindInfoOffset;
            ReadOnlySpan<byte> unwind = [1, 4, 1, 0, 4, 0x62, 0, 0];
            unwind.CopyTo(new Span<byte>((void*)(address + UnwindInfoOffset), unwind.Length));
            EntryPoint = Marshal.GetDelegateForFunctionPointer<T>(address);
            if (!NativeHookRelayMemory.FlushInstructionCache(NativeHookRelayMemory.GetCurrentProcess(), address, (nuint)code.Length) ||
                !NativeHookRelayMemory.RtlAddFunctionTable((nint)function, 1, (ulong)address))
                throw new Win32Exception("Could not initialize the native graphics relay.");
        }
        catch
        {
            NativeHookRelayMemory.VirtualFree(address, 0, 0x8000);
            throw;
        }
    }

    // Bind before enabling, using the original captured under Dalamud's hook
    // lock rather than the earlier vtable read. Another plugin may have
    // installed a hook between that read and our hook's registration.
    internal void BindOriginal(T target)
    {
        Interlocked.Exchange(ref *(nint*)(address + OriginalOffset), Marshal.GetFunctionPointerForDelegate(target));
        GC.KeepAlive(target);
    }

    internal void Enable() => Interlocked.And(ref State, int.MaxValue);

    internal void Bypass()
    {
        Interlocked.Or(ref State, int.MinValue);
        GC.KeepAlive(callback);
        // Intentionally retain the tiny native relay until process exit, as
        // Dalamud does for its own pointer-hook forwarding thunks.
    }

}

internal static class NativeHookRelayMemory
{
    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern nint VirtualAlloc(nint address, nuint size, uint allocationType, uint protection);
    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool VirtualFree(nint address, nuint size, uint freeType);
    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.U1)]
    internal static extern bool RtlAddFunctionTable(nint table, uint entries, ulong baseAddress);
    [DllImport("kernel32.dll")]
    internal static extern nint GetCurrentProcess();
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool FlushInstructionCache(nint process, nint address, nuint size);
}

using System.Runtime.InteropServices;

namespace AlphaChannel.Plugin.Video;

// Detects Wine at runtime via ntdll's wine_get_version export - the standard technique, doesn't
// depend on environment variables that may or may not propagate from the launcher. Used to keep
// Wine-specific compatibility handling from touching real Windows.
internal static class WineEnvironment
{
    private static readonly Lazy<bool> IsWineLazy = new(Detect);

    public static bool IsWine => IsWineLazy.Value;

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi)]
    private static extern nint GetModuleHandle(string moduleName);

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi)]
    private static extern nint GetProcAddress(nint module, string procName);

    private static bool Detect()
    {
        var ntdll = GetModuleHandle("ntdll.dll");
        return ntdll != nint.Zero && GetProcAddress(ntdll, "wine_get_version") != nint.Zero;
    }
}

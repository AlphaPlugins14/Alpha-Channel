using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Web.WebView2.Core;

namespace AlphaChannel.WebViewHost;

internal static class EvergreenSetup
{
    internal static bool IsInstalled()
    {
        try { return !string.IsNullOrEmpty(CoreWebView2Environment.GetAvailableBrowserVersionString()); }
        catch (WebView2RuntimeNotFoundException) { return false; }
    }
    internal static async Task<int> EnsureAsync(string directory)
    {
        if (IsInstalled()) return 0;
        Directory.CreateDirectory(directory);
        var installer = Path.Combine(directory, "MicrosoftEdgeWebview2Setup.exe");
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
            using var response = await client.GetAsync("https://go.microsoft.com/fwlink/p/?LinkId=2124703", HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();
            await using (var output = File.Create(installer))
                await response.Content.CopyToAsync(output);
            VerifyInstaller(installer);
            using var process = Process.Start(new ProcessStartInfo(installer, "/silent /install")
                { UseShellExecute = false, CreateNoWindow = true })!;
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(8));
            return IsInstalled() ? 0 : 4;
        }
        finally { try { File.Delete(installer); } catch { } }
    }
    private static void VerifyInstaller(string path)
    {
        var file = new TrustFile { Size = (uint)Marshal.SizeOf<TrustFile>(), Path = path };
        var pointer = Marshal.AllocHGlobal(Marshal.SizeOf<TrustFile>());
        try
        {
            Marshal.StructureToPtr(file, pointer, false);
            var trust = new TrustData { Size = (uint)Marshal.SizeOf<TrustData>(), UiChoice = 2, UnionChoice = 1, File = pointer };
            var action = new Guid("00AAC56B-CD44-11D0-8CC2-00C04FC295EE");
            if (WinVerifyTrust(new IntPtr(-1), ref action, ref trust) != 0)
                throw new InvalidOperationException("The WebView2 installer signature is invalid.");
            using var certificate = System.Security.Cryptography.X509Certificates.X509CertificateLoader.LoadCertificate(
                System.Security.Cryptography.X509Certificates.X509Certificate.CreateFromSignedFile(path).GetRawCertData());
            if (!certificate.Subject.Contains("O=Microsoft Corporation", StringComparison.Ordinal))
                throw new InvalidOperationException("The WebView2 installer publisher is invalid.");
        }
        finally { Marshal.DestroyStructure<TrustFile>(pointer); Marshal.FreeHGlobal(pointer); }
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct TrustFile { public uint Size; [MarshalAs(UnmanagedType.LPWStr)] public string Path; public IntPtr Handle, Subject; }
    [StructLayout(LayoutKind.Sequential)]
    private struct TrustData
    {
        public uint Size; public IntPtr Policy, Sip; public uint UiChoice, Revocation, UnionChoice;
        public IntPtr File; public uint StateAction; public IntPtr State, Url; public uint Flags, Context; public IntPtr Signature;
    }
    [DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern int WinVerifyTrust(IntPtr window, ref Guid action, ref TrustData data);
}

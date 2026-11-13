using System.Diagnostics;
using SharpDX.Direct3D11;

namespace AlphaChannel.Plugin.Video;

internal sealed class WebViewBrowserRenderer(Texture2D target, string cacheDirectory)
    : BrowserRendererBase(target, cacheDirectory)
{
    protected override string RuntimeFolder => "WebView";
    protected override string HostExecutable => "AlphaChannel.WebViewHost.exe";
    // WebView plays through Windows; captured audio is used only for broadcasting.
    protected override bool PlayCapturedAudioLocally => false;
    internal override void SetVolume(int value) => SendBinary(23, writer => writer.Write(Math.Clamp(value, 0, 200)));

    internal static async Task EnsureRuntimeAsync()
    {
        var directory = Path.Combine(Plugin.PluginInterface.AssemblyLocation.DirectoryName!, "Browsers", "WebView");
        using var runtime = new HelperRuntimeLease(directory);
        directory = runtime.DirectoryPath;
        var start = new ProcessStartInfo(Path.Combine(directory, "AlphaChannel.WebViewHost.exe"))
        {
            WorkingDirectory = directory, UseShellExecute = false, CreateNoWindow = true
        };
        start.ArgumentList.Add("--ensure-runtime");
        start.ArgumentList.Add(Path.Combine(Plugin.PluginInterface.ConfigDirectory.FullName, "Browser", "WebViewSetup"));
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Edge (WebView2) setup could not start.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch { try { process.Kill(true); } catch { } throw; }
        if (process.ExitCode != 0)
            throw new InvalidOperationException("Edge (WebView2) setup failed. Check your internet connection and try again, or install the Microsoft Edge WebView2 Evergreen Runtime manually.");
    }
}

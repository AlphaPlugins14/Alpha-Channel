using SharpDX.Direct3D11;
namespace AlphaChannel.Plugin.Video;

// CEF-specific host selection; frame transport is shared by browser implementations.
internal sealed class BrowserRenderer(Texture2D target, string cacheDirectory, string runtimeDirectory)
    : BrowserRendererBase(target, cacheDirectory, runtimeDirectory)
{
    protected override string RuntimeFolder => "Chromium";
    protected override string HostExecutable => "AlphaChannel.BrowserHost.exe";
}

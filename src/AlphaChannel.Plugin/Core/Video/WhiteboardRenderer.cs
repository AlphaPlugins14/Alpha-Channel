using SharpDX.Direct3D11;
using SharpDX.DXGI;

namespace AlphaChannel.Plugin.Video;

// A deliberately self-contained TV target for the experimental whiteboard.
// Removing this file and the small VideoEngine entry points removes the renderer.
internal sealed class WhiteboardRenderer : IDisposable
{
    internal const int Width = 1280;
    internal const int Height = 720;
    private readonly string renderKey;
    private readonly RenderResourceLifetime frameLifetime = new();
    private readonly byte[] pendingPixels = new byte[Width * Height * 4];
    private readonly Action uploadFrame;
    private bool framePending;

    internal Texture2D Texture { get; }
    private ShaderResourceView PreviewView { get; }
    internal nint PreviewHandle => PreviewView.NativePointer;

    internal WhiteboardRenderer(string renderKey = "AlphaChannel.Whiteboard")
    {
        this.renderKey = renderKey;
        uploadFrame = UploadPendingFrame;
        Texture = new Texture2D(
            DxHandler.Device,
            new Texture2DDescription
            {
                Width = Width,
                Height = Height,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.B8G8R8A8_UNorm,
                BindFlags = BindFlags.ShaderResource | BindFlags.RenderTarget,
                CpuAccessFlags = CpuAccessFlags.None,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
            });

        try { PreviewView = new ShaderResourceView(DxHandler.Device, Texture); }
        catch
        {
            Texture.Dispose();
            throw;
        }
    }

    internal void Upload(byte[] pixels)
    {
        if (pixels.Length != Width * Height * 4)
        {
            throw new ArgumentException("Whiteboard frame has the wrong dimensions.", nameof(pixels));
        }

        // Copy synchronously before the caller reuses its drawing buffer. The
        // lifetime gate prevents writes during native upload or after disposal.
        frameLifetime.Use(() =>
        {
            System.Buffer.BlockCopy(pixels, 0, pendingPixels, 0, pendingPixels.Length);
            framePending = true;
            DxHandler.RunOnRenderThread(renderKey, uploadFrame);
        });
    }

    private void UploadPendingFrame()
    {
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        frameLifetime.Use(() =>
        {
            if (!framePending) return;
            framePending = false;
            var acquired = System.Diagnostics.Stopwatch.GetTimestamp();
            unsafe
            {
                fixed (byte* pixelPointer = pendingPixels)
                {
                    DxHandler.Device?.ImmediateContext.UpdateSubresource(
                        Texture,
                        0,
                        null,
                        (nint)pixelPointer,
                        Width * 4,
                        0);
                }
            }
            PlaybackRenderDiagnostics.RecordUpload(
                System.Diagnostics.Stopwatch.GetElapsedTime(started, acquired).TotalMilliseconds,
                System.Diagnostics.Stopwatch.GetElapsedTime(acquired).TotalMilliseconds);
        });
    }

    internal void CancelPendingFrame() => frameLifetime.Use(() =>
    {
        framePending = false;
        DxHandler.CancelRenderThreadWork(renderKey);
    });

    public void Dispose()
    {
        frameLifetime.Dispose(() =>
        {
            DxHandler.CancelRenderThreadWork(renderKey);
            PreviewView.Dispose();
            Texture.Dispose();
        });
    }
}

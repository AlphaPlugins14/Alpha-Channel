using SharpDX.Direct3D11;

namespace AlphaChannel.Plugin.Video;

// One reusable GPU-only copy. Freshness is limited to the current Present;
// never restore a previous frame after a missing candidate or a zone change.
internal sealed class TvUiRendererCapture : IDisposable
{
    private Texture2D? texture;
    internal ShaderResourceView? View { get; private set; }
    internal bool Fresh { get; private set; }
    internal int Width { get; private set; }
    internal int Height { get; private set; }

    internal bool Capture(Device device, DeviceContext context, RenderTargetView source)
    {
        Fresh = false;
        using var resource = source.Resource;
        using var original = resource.QueryInterfaceOrNull<Texture2D>();
        if (original == null) return false;
        var d = original.Description;
        if (d.ArraySize != 1 || d.MipLevels != 1 || d.SampleDescription.Count != 1 ||
            source.Description.Dimension != RenderTargetViewDimension.Texture2D ||
            source.Description.Texture2D.MipSlice != 0) return false;
        if (texture == null || texture.Description.Width != d.Width || texture.Description.Height != d.Height ||
            texture.Description.Format != d.Format || View!.Description.Format != source.Description.Format)
        {
            Dispose();
            d.BindFlags = BindFlags.ShaderResource;
            d.Usage = ResourceUsage.Default;
            d.CpuAccessFlags = CpuAccessFlags.None;
            d.OptionFlags = ResourceOptionFlags.None;
            texture = new Texture2D(device, d);
            View = new ShaderResourceView(device, texture, new ShaderResourceViewDescription {
                Format = source.Description.Format,
                Dimension = SharpDX.Direct3D.ShaderResourceViewDimension.Texture2D,
                Texture2D = new() { MostDetailedMip = 0, MipLevels = 1 }
            });
            Width = d.Width; Height = d.Height;
        }
        context.CopyResource(original, texture);
        Fresh = true;
        return true;
    }

    internal void EndFrame() => Fresh = false;
    public void Dispose()
    {
        Fresh = false; View?.Dispose(); View = null;
        texture?.Dispose(); texture = null; Width = Height = 0;
    }
}

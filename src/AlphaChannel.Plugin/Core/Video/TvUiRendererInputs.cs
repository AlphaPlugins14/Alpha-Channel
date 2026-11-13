using SharpDX.Direct3D11;
using SharpDX.Direct3D;
using SharpDX.DXGI;

namespace AlphaChannel.Plugin.Video;

// Learn the colour textures sampled by the final backbuffer draw. A short
// history covers rotating intermediate targets without retaining them on zoning.
internal sealed class TvUiRendererInputs : IDisposable
{
    private sealed record Input(ShaderResourceView View, int Slot, string Description);
    private Dictionary<nint, Input> pending = [];
    private readonly List<Dictionary<nint, Input>> history = [];
    private int width, height;

    internal void SetOutputSize(int outputWidth, int outputHeight)
    {
        if (width == outputWidth && height == outputHeight) return;
        Clear(); width = outputWidth; height = outputHeight;
    }

    internal int Capture(DeviceContext context)
    {
        var views = context.PixelShader.GetShaderResources(0, 16);
        var added = 0;
        try
        {
            for (var slot = 0; slot < views.Length; slot++)
            {
                var view = views[slot];
                if (view == null || view.Description.Dimension != ShaderResourceViewDimension.Texture2D ||
                    view.Description.Texture2D.MostDetailedMip != 0) continue;
                using var resource = view.Resource;
                using var texture = resource.QueryInterfaceOrNull<Texture2D>();
                if (texture == null) continue;
                var d = texture.Description;
                if (d.Width != width || d.Height != height || d.ArraySize != 1 || d.SampleDescription.Count != 1 ||
                    (d.BindFlags & BindFlags.RenderTarget) == 0 || !ColourFormat(view.Description.Format) ||
                    pending.ContainsKey(resource.NativePointer) || pending.Count >= 16) continue;
                pending.Add(resource.NativePointer, new(view, slot, $"Final input {slot}: {d.Width}x{d.Height} {view.Description.Format}"));
                views[slot] = null!; // Transfer the getter's owned reference.
                added++;
            }
        }
        finally { foreach (var view in views) view?.Dispose(); }
        return added;
    }

    private static bool ColourFormat(Format format) => format is Format.B8G8R8A8_UNorm or Format.B8G8R8A8_UNorm_SRgb or
        Format.R8G8B8A8_UNorm or Format.R8G8B8A8_UNorm_SRgb or Format.R10G10B10A2_UNorm or
        Format.R16G16B16A16_Float or Format.R11G11B10_Float;

    internal bool TryFind(nint resource, out int slot)
    {
        for (var i = history.Count - 1; i >= 0; i--)
            if (history[i].TryGetValue(resource, out var input)) { slot = input.Slot; return true; }
        slot = -1; return false;
    }

    internal string Description => string.Join("; ", history.SelectMany(frame => frame.Values).Select(v => v.Description).Distinct());

    internal void EndFrame()
    {
        history.Add(pending); pending = [];
        while (history.Count > 3) { DisposeFrame(history[0]); history.RemoveAt(0); }
    }
    private static void DisposeFrame(Dictionary<nint, Input> frame) { foreach (var input in frame.Values) input.View.Dispose(); }
    internal void Clear()
    {
        DisposeFrame(pending); pending.Clear();
        foreach (var frame in history) DisposeFrame(frame);
        history.Clear();
    }
    public void Dispose() => Clear();
}

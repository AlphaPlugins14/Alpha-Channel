using SharpDX.Direct3D11;

namespace AlphaChannel.Plugin.Video;

internal static class TvUiRendererState
{
    // The regular painter restores the states it changes. An earlier injection
    // also needs to isolate optional geometry/tessellation stages and preserve
    // the exact blend factors, sample mask and stencil reference of the UI draw.
    internal static bool Draw(DeviceContext ctx, Func<bool> render)
    {
        using var geometry = ctx.GeometryShader.Get();
        using var hull = ctx.HullShader.Get();
        using var domain = ctx.DomainShader.Get();
        using var blend = ctx.OutputMerger.GetBlendState(out var factors, out var samples);
        using var depth = ctx.OutputMerger.GetDepthStencilState(out var stencil);
        try
        {
            ctx.GeometryShader.Set(null); ctx.HullShader.Set(null); ctx.DomainShader.Set(null);
            return render();
        }
        finally
        {
            ctx.GeometryShader.Set(geometry); ctx.HullShader.Set(hull); ctx.DomainShader.Set(domain);
            ctx.OutputMerger.SetBlendState(blend, factors, samples);
            ctx.OutputMerger.SetDepthStencilState(depth, stencil);
        }
    }
}

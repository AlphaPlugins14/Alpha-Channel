using SharpDX.Direct3D11;
using SharpDX.Mathematics.Interop;
using Buffer = SharpDX.Direct3D11.Buffer;

namespace AlphaChannel.Plugin.Video;

// Reused by a single painter under its frame lock. D3D getters AddRef the
// returned objects; restore and release those temporary references each frame.
internal sealed class ScreenShaderState
{
    private Buffer[] vertexBuffers = [], pixelBuffers = [];
    private ShaderResourceView[] resources = [];
    private SamplerState[] samplers = [];
    private readonly RawViewportF[] viewports = new RawViewportF[16];
    private int viewportCount;
    private bool captured;

    internal unsafe void Capture(DeviceContext context)
    {
        try
        {
            vertexBuffers = context.VertexShader.GetConstantBuffers(0, 1);
            pixelBuffers = context.PixelShader.GetConstantBuffers(0, 2);
            resources = context.PixelShader.GetShaderResources(0, 6);
            samplers = context.PixelShader.GetSamplers(0, 1);
            viewportCount = viewports.Length;
            // Avoid SharpDX's allocating generic GetViewports overload, which
            // has failed on this runtime. Use the bounded native array API.
            fixed (RawViewportF* pointer = viewports)
            {
                // ID3D11DeviceContext::RSGetViewports, slot 95 in the Windows
                // SDK interface. SharpDX exposes this native overload internally.
                var table = *(nint**)context.NativePointer;
                var get = (delegate* unmanaged[Stdcall]<nint, int*, RawViewportF*, void>)table[95];
                var count = viewportCount;
                get(context.NativePointer, &count, pointer);
                viewportCount = count;
            }
            captured = true;
        }
        catch { Release(); throw; }
    }

    internal unsafe void Restore(DeviceContext context)
    {
        if (!captured) return;
        try
        {
            context.VertexShader.SetConstantBuffers(0, vertexBuffers);
            context.PixelShader.SetConstantBuffers(0, pixelBuffers);
            context.PixelShader.SetShaderResources(0, resources);
            context.PixelShader.SetSamplers(0, samplers);
            fixed (RawViewportF* pointer = viewports)
                context.Rasterizer.SetViewports(pointer, viewportCount);
        }
        finally { Release(); }
    }

    private void Release()
    {
        captured = false;
        foreach (var value in vertexBuffers) value?.Dispose();
        foreach (var value in pixelBuffers) value?.Dispose();
        foreach (var value in resources) value?.Dispose();
        foreach (var value in samplers) value?.Dispose();
        Array.Clear(vertexBuffers); Array.Clear(pixelBuffers); Array.Clear(resources); Array.Clear(samplers);
    }
}

using System.Runtime.InteropServices;

namespace AlphaChannel.Plugin.Video;

// PSGetShader returns an owned COM reference. Keep it in a stack scope instead
// of allocating a SharpDX wrapper for every rejected game draw.
internal readonly unsafe struct TvUiRendererShader(nint pointer) : IDisposable
{
    internal nint Pointer => pointer;

    internal static TvUiRendererShader Get(nint context)
    {
        nint shader = 0;
        var table = *(nint**)context;
        // ID3D11DeviceContext::PSGetShader, Windows SDK slot 74. Neither class
        // instances nor their count are requested; the bound state is unchanged.
        var get = (delegate* unmanaged[Stdcall]<nint, nint*, nint*, uint*, void>)table[74];
        get(context, &shader, null, null);
        return new(shader);
    }

    public void Dispose()
    {
        if (pointer != 0) Marshal.Release(pointer);
    }
}

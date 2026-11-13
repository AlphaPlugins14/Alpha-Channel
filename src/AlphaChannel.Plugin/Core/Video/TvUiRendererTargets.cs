using SharpDX.Direct3D11;

namespace AlphaChannel.Plugin.Video;

// Per-frame references keep cache keys valid without retaining game render
// targets across loading screens or resize. No texture copies/readbacks.
internal sealed class TvUiRendererTargets : IDisposable
{
    internal readonly record struct Info(bool SameResource, bool Compatible, string Description, nint Resource, bool FullColourView);
    private readonly Dictionary<nint, (RenderTargetView View, Info Info)> views = [];
    private RenderTargetView? expected;
    private nint resourcePointer;

    internal Info Classify(RenderTargetView view, nint expectedView)
    {
        if (expected?.NativePointer != expectedView)
        {
            Clear();
            expected = new RenderTargetView(expectedView);
            ((SharpDX.IUnknown)expected).AddReference();
            using var resource = expected.Resource;
            resourcePointer = resource.NativePointer;
        }
        if (view.NativePointer == expectedView) return new(true, true, "Exact backbuffer view", resourcePointer, true);
        if (views.TryGetValue(view.NativePointer, out var cached)) return cached.Info;
        using var current = view.Resource;
        using var texture = current.QueryInterfaceOrNull<Texture2D>();
        var same = current.NativePointer == resourcePointer;
        var desc = view.Description;
        var expectedDesc = expected.Description;
        var compatible = same && desc.Dimension == expectedDesc.Dimension &&
            (desc.Dimension != RenderTargetViewDimension.Texture2D || desc.Texture2D.MipSlice == expectedDesc.Texture2D.MipSlice);
        var description = texture == null ? "Non-2D target" :
            $"{texture.Description.Width}x{texture.Description.Height} {texture.Description.Format}; samples {texture.Description.SampleDescription.Count}";
        var fullView = texture != null && desc.Dimension == RenderTargetViewDimension.Texture2D && desc.Texture2D.MipSlice == 0 &&
            texture.Description.ArraySize == 1 && texture.Description.SampleDescription.Count == 1;
        var info = new Info(same, compatible, description, current.NativePointer, fullView);
        if (views.Count < 128)
        {
            var owned = new RenderTargetView(view.NativePointer);
            ((SharpDX.IUnknown)owned).AddReference();
            views.Add(view.NativePointer, (owned, info));
        }
        return info;
    }

    internal void Clear()
    {
        foreach (var entry in views.Values) entry.View.Dispose();
        views.Clear(); expected?.Dispose(); expected = null; resourcePointer = 0;
    }
    public void Dispose() => Clear();
}

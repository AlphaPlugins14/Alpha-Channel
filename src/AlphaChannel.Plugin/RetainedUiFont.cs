using Dalamud.Bindings.ImGui;
using Dalamud.Interface.ManagedFontAtlas;

namespace AlphaChannel.Plugin;

// A managed atlas may temporarily report an existing handle unavailable while
// publishing a rebuild. Keep ownership of its last valid font/texture instead
// of changing typefaces for that frame. Refresh only on the first use in a new
// frame: every use in the frame must see the same font generation.
internal sealed class RetainedUiFont(IFontHandle handle) : IDisposable
{
    private ILockedImFont? loaded;
    private int resolvedFrame = -1;

    internal ImFontPtr ForFrame(int frame)
    {
        if (resolvedFrame == frame) return loaded?.ImFont ?? default;
        resolvedFrame=frame;
        if (handle.Available && handle.TryLock(out _) is { } replacement)
        {
            // A previous frame has finished rendering before the next Draw.
            // The retained lock owns the old atlas until this boundary; never
            // replace/release it halfway through a frame's draw commands.
            var previous=loaded;
            loaded=replacement;
            previous?.Dispose();
        }
        return loaded?.ImFont ?? default;
    }

    public void Dispose()
    {
        loaded?.Dispose();
        loaded=null;
    }
}

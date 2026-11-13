using System.Runtime.InteropServices;

namespace AlphaChannel.Plugin.Video;

internal static class ArcadeRaster
{
    // BGRA pixels can be filled in vectorised spans instead of four separately
    // bounds-checked byte stores per pixel. Clip before accessing the frame.
    internal static void FillRectangle(byte[] pixels, int width, int height,
        int x, int y, int fillWidth, int fillHeight, uint colour)
    {
        if (fillWidth <= 0 || fillHeight <= 0) return;
        var left = Math.Max(0, x);
        var top = Math.Max(0, y);
        var right = (int)Math.Min(width, (long)x + fillWidth);
        var bottom = (int)Math.Min(height, (long)y + fillHeight);
        if (right <= left || bottom <= top) return;
        var packed = colour | 0xFF000000u;
        var frame = MemoryMarshal.Cast<byte, uint>(pixels.AsSpan());
        if (left == 0 && right == width)
            frame.Slice(top * width, (bottom - top) * width).Fill(packed);
        else
            for (var row = top; row < bottom; row++)
                frame.Slice(row * width + left, right - left).Fill(packed);
    }
}

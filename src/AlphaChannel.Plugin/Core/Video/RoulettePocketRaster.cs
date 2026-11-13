using System.Runtime.InteropServices;

namespace AlphaChannel.Plugin.Video;

internal static class RoulettePocketRaster
{
    private readonly record struct Sample(short X, short Y, double Angle);
    private const int OuterRadius = 245;
    private const int InnerRadius = 174;
    private const double FullTurn = Math.PI * 2d;
    private const double PocketOffset = -Math.PI / 2d;
    // Fixed-size geometry, shared across rounds. Only the wheel rotation changes;
    // no per-frame trigonometry or growing collection of rotated frames is needed.
    private static readonly Sample[] samples = BuildSamples();

    private static Sample[] BuildSamples()
    {
        var result = new List<Sample>();
        for (var y = -OuterRadius; y <= OuterRadius; y++)
        for (var x = -OuterRadius; x <= OuterRadius; x++)
        {
            var squared = x * x + y * y;
            if (squared >= InnerRadius * InnerRadius && squared <= OuterRadius * OuterRadius)
                result.Add(new Sample((short)x, (short)y, Math.Atan2(y, x)));
        }
        return result.ToArray();
    }

    internal static void Draw(byte[] pixels, int width, int height,
        int centerX, int centerY, double wheelAngle, ReadOnlySpan<uint> colours)
    {
        if (colours.IsEmpty) return;
        var sectorAngle = FullTurn / colours.Length;
        var frame = MemoryMarshal.Cast<byte, uint>(pixels.AsSpan());
        foreach (var sample in samples)
        {
            var x = centerX + sample.X;
            var y = centerY + sample.Y;
            if ((uint)x >= (uint)width || (uint)y >= (uint)height) continue;
            // Preserve the original arithmetic/order at sector boundaries.
            var angle = (sample.Angle - wheelAngle - PocketOffset) % FullTurn;
            if (angle < 0d) angle += FullTurn;
            var index = Math.Min(colours.Length - 1, (int)(angle / sectorAngle));
            frame[y * width + x] = colours[index] | 0xFF000000u;
        }
    }
}

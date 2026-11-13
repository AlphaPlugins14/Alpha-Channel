using System.Numerics;

namespace AlphaChannel.Plugin.Video;

// Front-on samples of the Cinema, Ornate and Arcade branches in TvFrameShader.
// Keep their UV extents, bevels and decorative masks in step with that shader.
internal static class TvFramePreviewRaster
{
    internal const int Width = 640;
    internal const int Height = 400;

    internal static byte[] Render(TvFrameSelection frame)
    {
        var pixels = new byte[Width * Height * 4];
        for (var y = 0; y < Height; y++)
        for (var x = 0; x < Width; x++)
        {
            var p = new Vector2(((x + .5f) / Width * 2 - 1) * 1.14f,
                (1 - (y + .5f) / Height * 2) * 1.22f);
            var sample = Sample(p, frame);
            var i = (y * Width + x) * 4;
            pixels[i] = (byte)MathF.Round(Math.Clamp(sample.X, 0, 1) * 255);
            pixels[i + 1] = (byte)MathF.Round(Math.Clamp(sample.Y, 0, 1) * 255);
            pixels[i + 2] = (byte)MathF.Round(Math.Clamp(sample.Z, 0, 1) * 255);
            pixels[i + 3] = (byte)MathF.Round(sample.W * 255);
        }
        return pixels;
    }

    internal static Vector4 Sample(Vector2 p, TvFrameSelection frame)
    {
        var a = Vector2.Abs(p);
        if (a.X <= 1 && a.Y <= 1) return Vector4.Zero;
        var extent = frame.Style switch
        {
            TvFrameStyle.Cinema => new Vector2(.070f, .115f),
            TvFrameStyle.Ornate => new Vector2(.105f, .175f),
            TvFrameStyle.Arcade => new Vector2(.095f, .155f),
            _ => Vector2.Zero,
        };
        if (extent == Vector2.Zero) return Vector4.Zero;
        var across = MathF.Max((a.X - 1) / extent.X, (a.Y - 1) / extent.Y);
        if (across > 1) return Vector4.Zero;
        var bevel = MathF.Sin(Math.Clamp(across, 0, 1) * MathF.PI);
        var inner = 1 - Smooth(.06f, .18f, across);
        var outer = Smooth(.82f, 1, across);
        var light = Math.Clamp(.55f + .3f * p.Y - .16f * p.X, 0, 1);
        var c = new Vector3(.055f, .06f, .075f) + new Vector3(bevel * .075f * light);
        if (frame.Style == TvFrameStyle.Cinema)
        {
            if (a.X >= .90f && a.Y >= .82f)
                c += new Vector3(.20f, .18f, .14f) * (.5f + bevel);
            var screw = (a - new Vector2(1.035f, 1.055f)) / new Vector2(1, .6f);
            c = Vector3.Lerp(c, new Vector3(.5f, .48f, .43f), 1 - Smooth(.005f, .012f, screw.Length()));
        }
        if (frame.Style == TvFrameStyle.Ornate)
        {
            var grain = MathF.Sin(p.X * 110 + p.Y * 80 + MathF.Sin(p.Y * 35) * 2) * .018f;
            c = new Vector3(.18f, .075f, .027f) + new Vector3(grain) + bevel * new Vector3(.10f, .055f, .018f);
            var gold = MathF.Max(inner, outer * .7f);
            var scroll = .5f + .5f * MathF.Sin((a.X - a.Y) * 95 + MathF.Sin((a.X + a.Y) * 60) * 2);
            if (a.X >= .86f && a.Y >= .76f) gold = MathF.Max(gold, Smooth(.4f, .65f, scroll));
            c = Vector3.Lerp(c, new Vector3(.62f, .39f, .10f) * (.65f + bevel * .6f), gold);
        }
        if (frame.Style == TvFrameStyle.Arcade)
        {
            if (a.X > 1.04f && a.Y > 1.07f && (a.X - 1.04f) + (a.Y - 1.07f) * .6f > .07f)
                return Vector4.Zero;
            var accent = new Vector3(((frame.Colour >> 16) & 255) / 255f, ((frame.Colour >> 8) & 255) / 255f, (frame.Colour & 255) / 255f);
            if (a.X >= 1.025f && a.X <= 1.055f && a.Y >= .3f && a.Y <= .85f)
                c += p.Y > 0 ? accent : new Vector3(accent.Z, accent.Y, accent.X);
            if (a.Y >= 1.09f && a.Y <= 1.12f && a.X <= .96f) c = new Vector3(.55f, .35f, .09f);
        }
        return new Vector4(Vector3.Clamp(c, Vector3.Zero, Vector3.One), 1);
    }

    private static float Smooth(float low, float high, float value)
    {
        var t = Math.Clamp((value - low) / (high - low), 0, 1);
        return t * t * (3 - 2 * t);
    }
}

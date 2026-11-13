namespace AlphaChannel.Plugin;

// Group design sizes before applying the window scale. Never quantize native
// window scaling to the design groups: only the final pixel size is rounded.
internal static class UiFontSizing
{
    private static readonly float[] Groups = [.60f,.70f,.76f,.82f,.90f,1f,1.10f,1.20f,1.28f,1.35f,1.50f,1.65f,1.80f,2.05f,2.60f];

    internal static float Group(float relative)
    {
        if (!float.IsFinite(relative) || relative <= 0) return 1;
        return Groups.MinBy(group => MathF.Abs(group-relative));
    }

    internal static int Pixels(float basePixels, float relative = 1) =>
        RoundPixels(basePixels * Group(relative));

    internal static int RoundPixels(float pixels) =>
        float.IsFinite(pixels) ? Math.Clamp((int)MathF.Round(Math.Clamp(pixels,6,256)),6,256) : 13;
}

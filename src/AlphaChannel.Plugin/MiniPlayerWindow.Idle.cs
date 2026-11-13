using Dalamud.Bindings.ImGui;

namespace AlphaChannel.Plugin;

internal sealed partial class MiniPlayerWindow
{
    private void DrawScreensaver(ImDrawListPtr draw, Vector2 origin, Vector2 size)
    {
        draw.PushClipRect(origin, origin + size, true);
        DrawIdleBackground(draw, origin, size, screensaver: true);
        // Share the TV's artwork, animation clock and normalized bounce path.
        var logoSize = size * new Vector2(0.28f, 0.235f);
        var time = engine.ScreensaverAnimationTime;
        static float Bounce(float phase) => 1f - MathF.Abs((phase - MathF.Floor(phase)) * 2f - 1f);
        var position = origin + new Vector2(Bounce(time * 0.075f), Bounce(time * 0.103f + 0.31f)) * (size - logoSize);
        if (engine.ScreensaverTextureHandle != nint.Zero)
            draw.AddImage(new ImTextureID(unchecked((ulong)engine.ScreensaverTextureHandle)),
                position, position + logoSize);
        draw.PopClipRect();
    }

    private void DrawLoadingWheel(ImDrawListPtr draw, Vector2 origin, Vector2 size)
    {
        draw.PushClipRect(origin, origin + size, true);
        DrawIdleBackground(draw, origin, size, screensaver: false);
        var center = origin + size * new Vector2(0.5f, 0.43f);
        var radius = size.Y * 0.095f;
        var thickness = MathF.Max(1f, size.Y * 0.012f);
        var time = engine.IsShowingWaitingScreen ? engine.LoadingAnimationTime : (float)ImGui.GetTime();
        var phase = time * 3f;
        // Soft purple illumination behind the TV's fading ring and bright head.
        for (var halo = 16; halo > 0; halo--)
            draw.AddCircleFilled(center, radius * (1f + halo * 0.12f),
                ImGui.GetColorU32(new Vector4(0.15f, 0.035f, 0.32f, 0.012f)), 48);
        const int segments = 72;
        for (var segment = 0; segment < segments; segment++)
        {
            var angle = phase + segment * MathF.Tau / segments;
            var next = angle + MathF.Tau / segments;
            var brightness = MathF.Pow(segment / (float)(segments - 1), 2.4f);
            var ink = Vector4.Lerp(new Vector4(0.20f, 0.07f, 0.38f, 1f),
                new Vector4(0.72f, 0.28f, 1f, 1f), brightness);
            draw.AddLine(center + new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * radius,
                center + new Vector2(MathF.Cos(next), MathF.Sin(next)) * radius,
                ImGui.GetColorU32(ink), thickness);
        }
        var head = center + new Vector2(MathF.Cos(phase), MathF.Sin(phase)) * radius;
        draw.AddCircleFilled(head, thickness * 0.7f,
            ImGui.GetColorU32(new Vector4(0.78f, 0.38f, 1f, 1f)), 16);
        draw.PopClipRect();
    }

    private static void DrawIdleBackground(ImDrawListPtr draw, Vector2 origin, Vector2 size, bool screensaver)
    {
        var top = screensaver ? new Vector4(0.018f, 0.012f, 0.050f, 1f) : new Vector4(0.025f, 0.020f, 0.075f, 1f);
        var bottom = screensaver ? new Vector4(0.004f, 0.006f, 0.018f, 1f) : new Vector4(0.008f, 0.010f, 0.030f, 1f);
        draw.AddRectFilledMultiColor(origin, origin + size,
            ImGui.GetColorU32(top), ImGui.GetColorU32(top), ImGui.GetColorU32(bottom), ImGui.GetColorU32(bottom));
    }
}

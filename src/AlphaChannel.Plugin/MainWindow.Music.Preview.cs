using AlphaChannel.Plugin.Video;
using Dalamud.Bindings.ImGui;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    // Intentionally static: no clocks, media decoding or external artwork.
    private void DrawMusicPreview(float width, float height, bool text)
    {
        var origin = ImGui.GetCursorScreenPos();
        var size = new Vector2(width, height);
        ImGui.Dummy(size);
        var draw = ImGui.GetWindowDrawList();
        draw.PushClipRect(origin, origin + size, true);
        try
        {
            var scene = GetMusicEffects();
            uint Colour(uint rgb, float alpha = 1) => ImGui.GetColorU32(new Vector4(WheelRgb(rgb), alpha));
            Vector2 Point(float x, float y) => origin + new Vector2(width * x, height * y);
            draw.AddRectFilled(origin, origin + size, ImGui.GetColorU32(FrameBg), Ui(8));
            var fullStage = (scene.Mask & (64 | 128 | 256 | 2048 | 8192)) != 0;
            var tvHeight = Math.Min(height * .60f, width * (fullStage ? .50f : .68f) * 9 / 16);
            var tvWidth = tvHeight * 16 / 9;
            var min = origin + new Vector2((width - tvWidth) / 2, height * .56f - tvHeight / 2);
            var max = min + new Vector2(tvWidth, tvHeight);
            var centre = (min + max) / 2;
            var floor = max.Y + tvHeight * .10f;
            draw.AddLine(new Vector2(origin.X, floor), new Vector2(origin.X + width, floor), ImGui.GetColorU32(BorderSubtle), Ui(1));
            DrawMusicPreviewStageEffects(draw, scene, min, max);
            if ((scene.Mask & 2) != 0)
            {
                // Soft layered glow and arcs frame the stage without changing its geometry.
                for (var i = 8; i > 0; i--)
                {
                    var spread = tvHeight * i * .035f;
                    draw.AddRectFilled(min - new Vector2(spread), max + new Vector2(spread), Colour(scene.Halo, .018f), Ui(16));
                }
                for (var arc = 0; arc < 3; arc++)
                    draw.AddCircle(centre, tvHeight * (.70f + arc * .18f), Colour(scene.Halo, .28f - arc * .06f), 96, Ui(1.5f));
            }
            var speakerWidth = tvWidth * .17f;
            var speakerGap = tvWidth * .035f;
            for (var side = 0; side < 2; side++)
            {
                var left = side == 0;
                var speakerMin = new Vector2(left ? min.X - speakerGap - speakerWidth : max.X + speakerGap, min.Y + tvHeight * .03f);
                var speakerMax = speakerMin + new Vector2(speakerWidth, tvHeight * .97f);
                var speakerCentre = (speakerMin + speakerMax) / 2;
                if ((scene.Mask & 1) != 0)
                {
                    draw.AddRectFilled(speakerMin, speakerMax, ImGui.GetColorU32(new Vector4(.025f, .025f, .05f, 1)), Ui(3));
                    draw.AddRect(speakerMin, speakerMax, ImGui.GetColorU32(BorderSubtle), Ui(3));
                    for (var cone = 0; cone < 2; cone++)
                    {
                        var coneCentre = new Vector2(speakerCentre.X, speakerMin.Y + (speakerMax.Y - speakerMin.Y) * (cone == 0 ? .28f : .72f));
                        var radius = speakerWidth * .34f;
                        draw.AddCircle(coneCentre, radius, Colour(scene.Speakers, .18f), 32, Ui(7));
                        draw.AddCircle(coneCentre, radius, Colour(scene.Speakers), 32, Ui(2));
                        draw.AddCircleFilled(coneCentre, radius * .78f, 0xFF09090D, 32);
                        draw.AddCircle(coneCentre, radius * .68f, Colour(scene.Speakers, .45f), 32, Ui(1));
                        draw.AddCircleFilled(coneCentre, radius * .32f, Colour(scene.Speakers, .3f), 24);
                        draw.AddCircle(coneCentre, radius * .32f, Colour(scene.Speakers, .75f), 24, Ui(1));
                    }
                }
                if ((scene.Mask & 32) != 0)
                {
                    var emitter = new Vector2(left ? speakerMin.X - Ui(4) : speakerMax.X + Ui(4), speakerMax.Y);
                    for (var beam = 0; beam < 3; beam++)
                    {
                        var end = Point(left ? -.18f + beam * .06f : 1.18f - beam * .06f, .05f + beam * .16f);
                        draw.AddLine(emitter, end, Colour(scene.Lasers, .12f), Ui(6));
                        draw.AddLine(emitter, end, Colour(scene.Lasers, .85f), Ui(1.3f));
                    }
                }
                if ((scene.Mask & 4) != 0)
                    for (var led = 0; led < 10; led++)
                    {
                        var colour = scene.RainbowLeds ? MusicPreviewRainbow(led / 10f) : scene.Leds;
                        var y = speakerMin.Y + led * (speakerMax.Y - speakerMin.Y) / 10;
                        draw.AddRectFilled(new Vector2(speakerMin.X, y), new Vector2(speakerMin.X + Ui(3), y + (speakerMax.Y - speakerMin.Y) / 13), Colour(colour));
                        draw.AddRectFilled(new Vector2(speakerMax.X - Ui(3), y), new Vector2(speakerMax.X, y + (speakerMax.Y - speakerMin.Y) / 13), Colour(colour));
                    }
                if ((scene.Mask & 8) != 0)
                    for (var ring = 0; ring < 2; ring++) draw.AddCircle(speakerCentre, speakerWidth * (.65f + ring * .4f), Colour(scene.Rings, .45f - ring * .15f), 48, Ui(1));
            }
            if ((scene.Mask & 16) != 0)
                for (var i = 0; i < 18; i++)
                {
                    var x = i % 2 == 0 ? .07f + (i % 5) * .035f : .8f + (i % 5) * .035f;
                    var pos = Point(x, .1f + ((i * 7) % 17) / 20f);
                    draw.AddCircleFilled(pos, Ui(1.4f), Colour(scene.Sparkles, .8f), 8);
                }
            DrawMusicPreviewOuterEffects(draw, scene, min, max);
            // Screen is drawn last, masking decorations behind it just like the world TV.
            draw.AddRectFilled(min - new Vector2(Ui(4)), max + new Vector2(Ui(4)), 0xFF060609, Ui(3));
            draw.AddRectFilled(min, max, Colour(scene.Background));
            draw.AddRect(min - new Vector2(Ui(4)), max + new Vector2(Ui(4)), ImGui.GetColorU32(BorderSubtle), Ui(3));
            DrawMusicPreviewNeonFrame(draw, scene, min, max);
            draw.PushClipRect(min, max, true);
            DrawStaticMusicVisualiser(draw, min, new Vector2(tvWidth, tvHeight));
            if (text)
            {
                var engine = screenController.Engine;
                engine.SetMusicPreviewText(ResolveMusicScreenText(partyAudioScreenText));
                if (engine.MusicPreviewTextTexture != nint.Zero)
                {
                    var texture = new ImTextureID(unchecked((ulong)engine.MusicPreviewTextTexture));
                    DrawMusicTextBand(draw, texture, min, max - min, 0, 1f / 3, partyAudioScreenText.TopGradient, 0);
                    DrawMusicTextBand(draw, texture, min, max - min, 1f / 3, 2f / 3, partyAudioScreenText.MiddleGradient, 0);
                    DrawMusicTextBand(draw, texture, min, max - min, 2f / 3, 1, partyAudioScreenText.BottomGradient, 0);
                }
            }
            draw.PopClipRect();
        }
        finally { draw.PopClipRect(); }
    }
    private static void DrawMusicTextBand(
        ImDrawListPtr drawList, ImTextureID texture, Vector2 origin, Vector2 size,
        float bandStart, float bandEnd, AudioTextGradient gradient, float time)
    {
        var top = origin.Y + size.Y * bandStart;
        var bottom = origin.Y + size.Y * bandEnd;
        if (gradient == AudioTextGradient.Solid)
        {
            drawList.AddImage(texture,
                new Vector2(origin.X, top), new Vector2(origin.X + size.X, bottom),
                new Vector2(0f, bandStart), new Vector2(1f, bandEnd), uint.MaxValue);
            return;
        }

        // The shared text texture contains white glyphs and a dark shadow.
        // Tint narrow vertical slices to match the world TV's animated shader.
        const int slices = 64;
        for (var index = 0; index < slices; index++)
        {
            var left = (float)index / slices;
            var right = (float)(index + 1) / slices;
            var colour = SampleMusicTextGradient(gradient, (left + right) * 0.5f, time);
            drawList.AddImage(texture,
                new Vector2(origin.X + size.X * left, top),
                new Vector2(origin.X + size.X * right, bottom),
                new Vector2(left, bandStart), new Vector2(right, bandEnd),
                ImGui.GetColorU32(new Vector4(colour, 1f)));
        }
    }

    private static Vector3 SampleMusicTextGradient(
        AudioTextGradient gradient, float x, float time)
    {
        if (gradient == AudioTextGradient.Rainbow)
        {
            var phase = MusicFraction(x * 0.9f - time * 0.08f);
            return new Vector3(
                MusicRainbowChannel(phase),
                MusicRainbowChannel(phase + 2f / 3f),
                MusicRainbowChannel(phase + 1f / 3f));
        }

        var wave = 0.5f + 0.5f * MathF.Sin(
            2f * MathF.PI * (x * 0.85f - time * 0.12f));
        return gradient switch
        {
            AudioTextGradient.Aurora => Vector3.Lerp(
                new Vector3(0.20f, 0.96f, 0.68f),
                new Vector3(0.72f, 0.40f, 1f), wave),
            AudioTextGradient.Sunset => Vector3.Lerp(
                new Vector3(1f, 0.68f, 0.22f),
                new Vector3(1f, 0.28f, 0.66f), wave),
            _ => Vector3.Lerp(
                new Vector3(0.18f, 0.82f, 1f),
                new Vector3(0.24f, 0.38f, 1f), wave),
        };
    }

    private static float MusicFraction(float value) => value - MathF.Floor(value);

    private static float MusicRainbowChannel(float phase) =>
        Math.Clamp(MathF.Abs(MusicFraction(phase) * 6f - 3f) - 1f, 0f, 1f);

    private static uint MusicPreviewRainbow(float t)
    {
        var hue = t * MathF.PI * 2;
        return WheelHex(new Vector3(.5f + .5f * MathF.Sin(hue), .5f + .5f * MathF.Sin(hue + 2.094f), .5f + .5f * MathF.Sin(hue + 4.189f)));
    }
    private static uint MusicPreviewTheme(AudioVisualizerTheme theme, float t) => theme switch
    {
        AudioVisualizerTheme.Rainbow => MusicPreviewRainbow(t),
        AudioVisualizerTheme.ElectricBlue => 0x357BFF, AudioVisualizerTheme.NeonCyan => 0x00DDFF,
        AudioVisualizerTheme.Emerald => 0x00E89C, AudioVisualizerTheme.HotPink => 0xFF339D,
        AudioVisualizerTheme.SunsetOrange => 0xFF8B24, AudioVisualizerTheme.Crimson => 0xEB2750,
        AudioVisualizerTheme.White => 0xFFFFFF,
        AudioVisualizerTheme.PurplePink => WheelHex(Vector3.Lerp(WheelRgb(0x9F39FF), WheelRgb(0xFF399F), t)),
        AudioVisualizerTheme.BlueCyan => WheelHex(Vector3.Lerp(WheelRgb(0x357BFF), WheelRgb(0x00DDFF), t)),
        AudioVisualizerTheme.Sunset => WheelHex(Vector3.Lerp(WheelRgb(0xFF8B24), WheelRgb(0xFF339D), t)),
        AudioVisualizerTheme.Yellow => 0xFFDC28,
        AudioVisualizerTheme.Lime => 0xA0FF30,
        AudioVisualizerTheme.Lavender => 0xC4A0FF,
        AudioVisualizerTheme.BluePurple => WheelHex(Vector3.Lerp(WheelRgb(0x357BFF), WheelRgb(0x9F39FF), t)),
        AudioVisualizerTheme.PinkCyan => WheelHex(Vector3.Lerp(WheelRgb(0xFF339D), WheelRgb(0x00DDFF), t)),
        AudioVisualizerTheme.GreenYellow => WheelHex(Vector3.Lerp(WheelRgb(0x00E89C), WheelRgb(0xFFDC28), t)),
        AudioVisualizerTheme.Aurora => WheelHex(Vector3.Lerp(WheelRgb(0x00DDFF), WheelRgb(0x9F39FF), 1f - MathF.Abs(2f * t - 1f))),
        AudioVisualizerTheme.CandyWave => WheelHex(Vector3.Lerp(WheelRgb(0xFF339D), WheelRgb(0x00DDFF), .5f - .5f * MathF.Cos(4f * MathF.PI * t))),
        AudioVisualizerTheme.Halloween => WheelHex(Vector3.Lerp(WheelRgb(0xFF770F), WheelRgb(0x9F39FF), t)),
        _ => 0xAD7AFF
    };
}

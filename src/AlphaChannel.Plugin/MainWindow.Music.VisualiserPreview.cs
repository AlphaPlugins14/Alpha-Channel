using AlphaChannel.Plugin.Video;
using Dalamud.Bindings.ImGui;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    // Representative, deterministic samples: previews never decode audio or animate.
    private void DrawStaticMusicVisualiser(ImDrawListPtr draw, Vector2 origin, Vector2 size)
    {
        Vector2 Point(float x, float y) => origin + size * new Vector2(x, y);
        uint Colour(float x, float alpha = 1) => ImGui.GetColorU32(new Vector4(
            WheelRgb(MusicPreviewTheme(partyVisualizerTheme, x)), alpha));

        if (partyVisualizerMode == AudioVisualizerMode.ClassicBars)
        {
            // Match AudioPS: thirteen narrow bars in the middle 36% of the screen,
            // with their shared baseline below centre and ample space around them.
            for (var bar = 0; bar < 13; bar++)
            {
                var t = bar / 12f;
                var half = .045f + .055f * (.5f + .5f * MathF.Sin(bar * 1.37f + .8f));
                var x = .32f + t * .36f;
                var colour = partyVisualizerTheme == AudioVisualizerTheme.AlphaPurple
                    ? ImGui.GetColorU32(new Vector4(WheelRgb(0xAD7AFF), 1)) : Colour(t);
                draw.AddRectFilled(Point(x - .007f, .63f - half), Point(x + .007f, .63f + half), colour);
            }
            return;
        }

        if (partyVisualizerMode is AudioVisualizerMode.FfmpegSpectrum or AudioVisualizerMode.MirrorSpectrum)
        {
            // Contiguous stepped frequency areas, rather than disconnected equalizer bars.
            // Typical music has stronger bass on the left and finer detail on the right.
            const int bins = 96;
            for (var bin = 0; bin < bins; bin++)
            {
                var t = bin / (float)bins;
                var envelope = .08f + .54f * MathF.Exp(-t * 3.7f);
                var detail = .76f + .24f * MathF.Abs(MathF.Sin(bin * 2.17f));
                var peak = .14f * MathF.Exp(-MathF.Pow((t - .20f) / .045f, 2));
                var amplitude = Math.Min(.8f, envelope * detail + peak);
                if (partyVisualizerMode == AudioVisualizerMode.MirrorSpectrum)
                    draw.AddRectFilled(Point(t, .5f - amplitude * .5f), Point((bin + 1f) / bins, .5f + amplitude * .5f), Colour(t));
                else
                    draw.AddRectFilled(Point(t, 1 - amplitude), Point((bin + 1f) / bins, 1), Colour(t));
            }
            return;
        }

        if (partyVisualizerMode == AudioVisualizerMode.NeonWave)
        {
            float Sample(float t) => .5f + (.30f - .18f * t) *
                (MathF.Sin(t * 191) * MathF.Pow(MathF.Abs(MathF.Sin(t * 43 + .7f)), 5) + .12f * MathF.Sin(t * 337));
            const int samples = 240;
            for (var sample = 0; sample < samples; sample++)
            {
                var t = sample / (float)samples;
                var next = (sample + 1f) / samples;
                var a = Point(t, Sample(t));
                var b = Point(next, Sample(next));
                draw.AddLine(a, b, Colour(t, .14f), Ui(5));
                draw.AddLine(a, b, ImGui.GetColorU32(new Vector4(0, 0, 0, .65f)), Ui(2.5f));
                draw.AddLine(a, b, Colour(t), Ui(1.2f));
                draw.AddLine(Point(t, .5f), Point(next, .5f), Colour(t, .55f), Ui(.7f));
            }
            return;
        }

        if (partyVisualizerMode == AudioVisualizerMode.Waterfall)
        {
            // Scrolling spectrogram snapshot: dense low frequencies below, sparse
            // horizontal harmonics above, and irregular vertical history throughout.
            const int columns = 96, rows = 48;
            for (var row = 0; row < rows; row++)
            for (var column = 0; column < columns; column++)
            {
                var x = column / (float)columns;
                var y = row / (float)rows;
                var history = .5f + .5f * MathF.Sin(column * .38f + .3f * MathF.Sin(column * 1.9f));
                var grain = .5f + .5f * MathF.Sin(column * 12.9898f + row * 78.233f);
                var harmonic = MathF.Pow(MathF.Abs(MathF.Sin(row * .83f + history * .7f)), 12);
                var energy = y > .55f ? .45f + .5f * history - .22f * grain :
                    harmonic * (.18f + history * .65f) * (.4f + y) * grain;
                draw.AddRectFilled(Point(x, y), Point((column + 1f) / columns, (row + 1f) / rows), Colour(x, Math.Clamp(energy, 0, 1)));
            }
            return;
        }

        if (partyVisualizerMode == AudioVisualizerMode.OrbitalScope)
        {
            // Stereo polar traces form a broad fan with fading layers, rather
            // than the dotted circular placeholder used by the old preview.
            const int traces = 16, samples = 100;
            for (var trace = 0; trace < traces; trace++)
            {
                Vector2 Sample(float t)
                {
                    var angle = t * MathF.PI * 2;
                    var depth = (1 - MathF.Cos(angle)) * .5f;
                    var spread = .30f + trace * .007f;
                    var jitter = .006f * MathF.Sin(t * 281 + trace * 1.7f);
                    var x = .5f + MathF.Sin(angle) * spread * MathF.Sqrt(depth) + jitter;
                    var y = .06f + depth * (.78f + trace * .006f);
                    return new Vector2(x, y);
                }
                for (var sample = 0; sample < samples; sample++)
                {
                    var a = Sample(sample / (float)samples);
                    var b = Sample((sample + 1f) / samples);
                    draw.AddLine(Point(a.X, a.Y), Point(b.X, b.Y), Colour(a.X, .18f + trace * .025f), Ui(.9f));
                }
            }
        }
    }
}

namespace AlphaChannel.Plugin.Video;

internal enum AudioVisualizerMode
{
    ClassicBars,
    FfmpegSpectrum,
    MirrorSpectrum,
    NeonWave,
    Waterfall,
    OrbitalScope
}

internal static class AudioVisualizerFilter
{
    internal const int FrameWidth = 640;
    internal const int FrameHeight = 360;
    // Generate 640x360 frames and retain that size through MPV and texture
    // upload. The TV and mini-player GPU samplers perform display scaling.
    // Use the full frame: font-rendered text is composited separately on top,
    // so the visualizer does not need reserved borders for its labels.
    // Preserve the live audio route while removing every video-generating
    // branch. Clearing lavfi-complex entirely can end an active radio stream.
    internal const string AudioPassthroughGraph = "[aid1]anull[ao]";

    internal static string GetGraph(
        AudioVisualizerMode mode,
        AudioVisualizerTheme theme,
        VisualizerQuality quality = VisualizerQuality.Full)
    {
        var graph = mode switch
        {
            AudioVisualizerMode.FfmpegSpectrum =>
                BuildFrequencyBarsGraph(theme),

            AudioVisualizerMode.MirrorSpectrum =>
                BuildMirrorSpectrumGraph(theme),

            AudioVisualizerMode.NeonWave =>
                BuildNeonWaveGraph(theme),

            AudioVisualizerMode.Waterfall =>
                BuildWaterfallGraph(theme),

            AudioVisualizerMode.OrbitalScope =>
BuildOrbitalScopeGraph(theme),

            // Classic Bars are drawn by ScreenPainter from the lightweight
            // astats level measurement. Leave lavfi-complex empty so MPV sends
            // live radio audio through its native output path.
            _ => string.Empty
        };
        if (quality == VisualizerQuality.Full || graph.Length == 0) return graph;
        var minimal = quality == VisualizerQuality.Minimal;
        graph = graph.Replace("size=640x360", minimal ? "size=240x136" : "size=320x180")
            .Replace("size=640x180", minimal ? "size=240x68" : "size=320x90")
            .Replace("size=400x180", minimal ? "size=160x72" : "size=240x108")
            .Replace("rate=20:", minimal ? "rate=6:" : "rate=10:")
            .Replace("rate=15:", minimal ? "rate=6:" : "rate=8:")
            .Replace("fps=15:", minimal ? "fps=6:" : "fps=8:");
        // Keep the output texture size stable; only analysis/colour/glow work
        // happens at reduced resolution. Square pixels prevent letterboxing.
        if (!graph.Contains("scale=640:360", StringComparison.Ordinal))
        {
            var output = graph.LastIndexOf("[vo]", StringComparison.Ordinal);
            graph = graph.Insert(output, ",scale=640:360:flags=bilinear,setsar=1");
        }
        return graph;
    }

    internal static int GetFrameRate(AudioVisualizerMode mode, VisualizerQuality quality) =>
        quality == VisualizerQuality.Minimal ? 6 :
        mode is AudioVisualizerMode.FfmpegSpectrum or AudioVisualizerMode.MirrorSpectrum
            ? quality == VisualizerQuality.Full ? 20 : 10
            : quality == VisualizerQuality.Full ? 15 : 8;

    private static string BuildFrequencyBarsGraph(
        AudioVisualizerTheme theme)
    {
        return
            "[aid1]asplit[ao][visualizer];" +

            "[visualizer]" +
            "aformat=channel_layouts=mono," +
            // Headroom only on the visualizer branch; [ao] retains original volume.
            "volume=0.5," +

            "showfreqs=" +
            "size=640x360:" +
            "rate=20:" +
            "mode=bar:" +
            "ascale=sqrt:" +
            "fscale=log:" +
            "win_size=256:" +
            "win_func=hann:" +
            "overlap=0.75:" +
            // FFmpeg's multi-frame averaging can divide by zero at full-height
            // peaks and poison the stored height. Use each fresh FFT frame.
            "averaging=1:" +
            BuildThemeFilter(theme) +
            "[vo]";
    }

    private static string BuildMirrorSpectrumGraph(
        AudioVisualizerTheme theme)
    {
        return
            "[aid1]asplit[ao][visualizer];" +

            "[visualizer]" +
            "aformat=channel_layouts=mono," +
            // Headroom only on the visualizer branch; [ao] retains original volume.
            "volume=0.5," +

            "showfreqs=" +
            "size=640x180:" +
            "rate=20:" +
            "mode=bar:" +
            "ascale=sqrt:" +
            "fscale=log:" +
            "win_size=256:" +
            "win_func=hann:" +
            "overlap=0.75:" +
            // FFmpeg's multi-frame averaging can divide by zero at full-height
            // peaks and poison the stored height. Use each fresh FFT frame.
            "averaging=1:" +
            BuildThemeFilter(theme) +

            "[spectrum];" +

            "[spectrum]" +
            "split=2" +
            "[upper][lower];" +

            "[lower]" +
            "vflip" +
            "[lowerFlipped];" +

            "[upper][lowerFlipped]" +
            "vstack=inputs=2" +
            "[vo]";
    }

    private static string BuildWaterfallGraph(
    AudioVisualizerTheme theme)
    {
        return
            "[aid1]asplit[ao][visualizer];" +

            "[visualizer]" +
            "aformat=channel_layouts=mono," +

            //
            // Create a scrolling grayscale spectrogram. New frequency data
            // enters from the right and previous history moves left.
            //
            "showspectrum=" +
            "size=400x180:" +
            "slide=scroll:" +
            "mode=combined:" +
            "color=intensity:" +
            "scale=sqrt:" +
            "fscale=log:" +
            "saturation=0:" +
            "win_func=hann:" +
            "orientation=vertical:" +
            "overlap=1:" +
           "gain=1:" +
            "fps=15:" +
            "legend=0," +

            //
            // Apply the same solid, gradient or rainbow theme used by the other
            // FFmpeg visualizers.
            //
            BuildVideoThemeFilter(theme) +
            "," +

            // Scaling the 400x180 source preserves its display aspect by changing
            // pixel aspect. Reset to square pixels so MPV fills the 16:9 TV.
            "scale=640:360:flags=bilinear,setsar=1" +
            "[vo]";
    }

    private static string BuildThemeFilter(
    AudioVisualizerTheme theme)
    {
        //
        // showfreqs and showwaves first draw a white visualizer. The common
        // video-theme filter then colours that white intensity image.
        //
        return
            "colors=0xFFFFFF," +
            BuildVideoThemeFilter(theme);
    }

    private static string BuildVideoThemeFilter(
    AudioVisualizerTheme theme)
    {
        return theme switch
        {
            AudioVisualizerTheme.ElectricBlue =>
                BuildSolidColour(
                    59,
                    130,
                    246),

            AudioVisualizerTheme.NeonCyan =>
                BuildSolidColour(
                    34,
                    211,
                    238),

            AudioVisualizerTheme.Emerald =>
                BuildSolidColour(
                    52,
                    211,
                    153),

            AudioVisualizerTheme.HotPink =>
                BuildSolidColour(
                    244,
                    114,
                    182),

            AudioVisualizerTheme.SunsetOrange =>
                BuildSolidColour(
                    251,
                    146,
                    60),

            AudioVisualizerTheme.Crimson =>
                BuildSolidColour(
                    239,
                    68,
                    68),

            AudioVisualizerTheme.White =>
                BuildSolidColour(
                    248,
                    250,
                    252),

            AudioVisualizerTheme.PurplePink =>
                BuildGradient(
                    168, 85, 247,
                    244, 114, 182),

            AudioVisualizerTheme.BlueCyan =>
                BuildGradient(
                    59, 130, 246,
                    34, 211, 238),

            AudioVisualizerTheme.Sunset =>
                BuildGradient(
                    249, 115, 22,
                    250, 204, 21),

            AudioVisualizerTheme.Rainbow =>
                "format=rgb24," +
                "geq=" +
                "r='r(X,Y)*(127.5+127.5*sin(2*PI*X/W))/255':" +
                "g='g(X,Y)*(127.5+127.5*sin(2*PI*X/W+2*PI/3))/255':" +
                "b='b(X,Y)*(127.5+127.5*sin(2*PI*X/W+4*PI/3))/255'",

            AudioVisualizerTheme.Yellow => BuildSolidColour(255, 220, 40),
            AudioVisualizerTheme.Lime => BuildSolidColour(160, 255, 48),
            AudioVisualizerTheme.Lavender => BuildSolidColour(196, 160, 255),
            AudioVisualizerTheme.BluePurple => BuildGradient(53, 123, 255, 159, 57, 255),
            AudioVisualizerTheme.PinkCyan => BuildGradient(255, 51, 157, 0, 221, 255),
            AudioVisualizerTheme.GreenYellow => BuildGradient(0, 232, 156, 255, 220, 40),
            AudioVisualizerTheme.Aurora => BuildGradient(0, 221, 255, 159, 57, 255, "1-abs(2*X/W-1)"),
            AudioVisualizerTheme.CandyWave => BuildGradient(255, 51, 157, 0, 221, 255, "0.5-0.5*cos(4*PI*X/W)"),
            AudioVisualizerTheme.Halloween => BuildGradient(255, 119, 15, 159, 57, 255),

            _ =>
                BuildSolidColour(
                    168,
                    85,
                    247)
        };
    }

    private static string BuildNeonWaveGraph(
    AudioVisualizerTheme theme)
    {
        return
            "[aid1]asplit[ao][visualizer];" +

            "[visualizer]" +
            "aformat=channel_layouts=mono," +

            //
            // Generate the waveform at a lower resolution and frame rate.
            //
            "showwaves=" +
            "size=400x180:" +
            "rate=15:" +
            "mode=line:" +
            "scale=lin:" +
            BuildThemeFilter(theme) +

            "[waveRgb];" +

            //
            // Force a consistent planar RGB format before splitting. The
            // previous glow attempt allowed FFmpeg to negotiate incompatible
            // formats between the sharp and blurred branches.
            //
            "[waveRgb]" +
            "format=gbrp," +
            "split=2" +
            "[sharp][glowSource];" +

            //
            // A small Gaussian blur creates the glow. It is performed before
            // upscaling, keeping its CPU cost relatively low.
            //
            "[glowSource]" +
            "gblur=sigma=1:steps=1" +
            "[glow];" +

            //
            // Combine the blurred and sharp copies. Explicitly convert the
            // result back to RGB before scaling.
            //
            "[glow][sharp]" +
            "blend=all_mode=screen:all_opacity=0.55," +
            "format=rgb24," +

            // Scaling the 400x180 source preserves its display aspect by changing
            // pixel aspect. Reset to square pixels so MPV fills the 16:9 TV.
            "scale=640:360:flags=bilinear,setsar=1" +
            "[vo]";
    }

    private static string BuildOrbitalScopeGraph(
    AudioVisualizerTheme theme)
    {
        return
            "[aid1]asplit[ao][visualizer];" +

            //
            // Orbital Scope compares the left and right audio channels, so it
            // must retain stereo input instead of converting the audio to mono.
            //
            "[visualizer]" +
            "aformat=channel_layouts=stereo," +

            //
            // Render a low-resolution polar vectorscope. The modest resolution
            // and 15 FPS rate keep its CPU use below the more elaborate effects.
            //
            "avectorscope=" +
            "size=400x180:" +
            "rate=15:" +
            "mode=polar:" +
            "draw=line:" +
            "scale=sqrt:" +
            "zoom=1.15:" +

            //
            // Draw a white source image so the normal theme-processing stage can
            // apply solid colours, gradients and rainbow colouring consistently.
            //
            "rc=255:" +
            "gc=255:" +
            "bc=255:" +
            "ac=255:" +

            //
            // Retain a short, inexpensive fading trail without using blur.
            //
            "rf=8:" +
            "gf=8:" +
            "bf=8:" +
            "af=8," +

            BuildVideoThemeFilter(theme) +
            "," +

            // Scaling the 400x180 source preserves its display aspect by changing
            // pixel aspect. Reset to square pixels so MPV fills the 16:9 TV.
            "scale=640:360:flags=bilinear,setsar=1" +
            "[vo]";
    }

    private static string BuildSolidColour(
    int red,
    int green,
    int blue)
    {
        return
            "format=rgb24," +
            "geq=" +
            $"r='r(X,Y)*{red}/255':" +
            $"g='g(X,Y)*{green}/255':" +
            $"b='b(X,Y)*{blue}/255'";
    }

    private static string BuildGradient(
        int startR,
        int startG,
        int startB,
        int endR,
        int endG,
        int endB,
        string position = "X/W")
    {
        return
            "format=rgb24," +
            "geq=" +
            $"r='r(X,Y)*({startR}+({endR}-{startR})*({position}))/255':" +
            $"g='g(X,Y)*({startG}+({endG}-{startG})*({position}))/255':" +
            $"b='b(X,Y)*({startB}+({endB}-{startB})*({position}))/255'";
    }
}
